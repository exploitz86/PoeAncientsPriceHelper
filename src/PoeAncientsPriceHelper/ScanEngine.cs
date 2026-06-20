using System.Diagnostics;
using System.Drawing;

namespace PoeAncientsPriceHelper;

internal sealed class ScanEngine : IDisposable
{
    private readonly AppConfig _config;
    private readonly PriceRepository _prices;
    private readonly IconCache _icons;
    private readonly IScreenCaptureBackend _capture;
    private CancellationTokenSource? _cts;
    private Task? _loopTask;
    private Dictionary<string, int> _lastPositions = new();
    private string _logPath = null!;   // assigned in RunLoopAsync before any Log() call

    // Resolution cache for the exact → prefix → fuzzy chain in BuildPriceRows. The same OCR'd
    // names recur on every pass while a panel is open, so caching the resolved price key (or a
    // recorded miss) skips the dictionary scan + Levenshtein work on all but the first pass.
    // Invalidated wholesale when the price snapshot changes (tracked via PriceGeneration).
    private int _cachedPriceGeneration = -1;
    private readonly Dictionary<string, (string? Key, bool Exact)> _resolutionCache = new();

    // Shared with the global hotkey hook (App). The loop owns the detection state, so the hook
    // only sets a "dismissed" latch; the loop reads it and keeps the overlay hidden.
    private static volatile bool _dismissed;
    private static volatile bool _showing;

    public bool IsRunning => _loopTask is { IsCompleted: false };

    // True while the overlay is actually showing a confirmed panel.
    public static bool IsShowing => _showing;

    // ESC / Left-Ctrl+click: hide the overlay and keep it hidden until the panel actually closes
    // (ESC closes the panel, so it clears fast; Ctrl+click leaves the panel open, so it stays
    // dismissed without flickering until the user closes the panel themselves).
    public static void RequestDismiss() => _dismissed = true;

    public ScanEngine(AppConfig config, PriceRepository prices, IconCache icons, IScreenCaptureBackend capture)
    {
        _config = config;
        _prices = prices;
        _icons = icons;
        _capture = capture;
    }

    public void Start()
    {
        if (IsRunning) return;
        // Reset shared static flags so a stale loop (e.g. one that timed out in StopAndWait)
        // can't clobber the new instance's dismiss/show state.
        _dismissed = false;
        _showing = false;
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        _loopTask = Task.Run(() => RunLoopAsync(_cts.Token));
    }

    public void StopAndWait(TimeSpan timeout)
    {
        _cts?.Cancel();
        try { _loopTask?.Wait(timeout); } catch { }
    }

    private void Log(string msg)
    {
        // File logging is debug-only: in normal use the loop fires ~10×/s and would otherwise
        // churn the log file continuously (a real cost on the hot path).
        if (!App.DebugMode) return;
        var line = $"[{DateTime.Now:HH:mm:ss.fff}] {msg}";
        try { File.AppendAllText(_logPath, line + "\n"); } catch { }
        Console.WriteLine(line);
    }

    private async Task RunLoopAsync(CancellationToken ct)
    {
        // Keep _logPath assigned even when not debugging so Log() never throws on a null/empty
        // path; only truncate the file when debug logging is actually enabled.
        _logPath = Path.Combine(AppContext.BaseDirectory, "scan_log.txt");
        if (App.DebugMode)
            File.WriteAllText(_logPath, "");

        Log($"START prices={_prices.ItemCount} icons={_icons.IsAvailable} region={_config.RegionRect}");

        var scanner = new OcrScanner(Log, App.DebugMode);
        var detector = new ListDetector();
        var sw = Stopwatch.StartNew();
        var slots = new List<RowSlot>();             // per-row accumulator: priced rows lock, misses keep retrying
        IReadOnlyList<PriceRow> lastRows = [];       // what the overlay shows
        var quantityMemory = new Dictionary<string, (int Multiplier, DateTime ExpiresUtc)>(StringComparer.Ordinal);
        var scrollHoldoffUntil = DateTime.MinValue;
        int topmostCounter = 0;
        const int TopmostEveryN = 10;
        bool isOpen = false;          // brightness gate: bright enough to attempt OCR
        bool confirmedOpen = false;   // OCR actually found a list — only then show the overlay
        // After a dismiss (ESC / Ctrl+click) the brightness gate can re-trip on ambient light that
        // grazes the threshold (the game world after the panel closes reads almost as bright as a real
        // panel — measured 105 vs a real panel's 101). That re-show is the post-ESC flicker. While this
        // is set, the brightness-only "reading…" hint is suppressed: nothing shows until OCR actually
        // confirms a priced row again. Cleared on the next real confirm.
        bool suppressHintUntilConfirm = false;
        int brightStreak = 0;
        int darkStreak = 0;
        int dismissDark = 0;          // dark frames seen while dismissed — releases the latch when the panel closes
        int cycleCount = 0;
        var lastOcrAt = DateTime.MinValue;
        int ocrEmptyStreak = 0;                      // consecutive OCR passes with no usable priced rows
        const int MinOcrIntervalMs = 75;             // OCR floor while panel is open — faster refresh while scrolling
        const int OpenCycleMs = 75;                  // tight loop while scanning
        const int ClosedCycleMs = 150;               // polling while watching for the panel — snappy detection
        const int ClearAfterEmptyOcr = 2;            // clear stale rows after ~200 ms of consecutive OCR misses
        const int ScrollHoldoffMs = 125;             // suppress low-confidence rows briefly during active scroll motion
        const int DarkToRelease = 3;                 // dark frames before a dismiss latch releases
        // Asymmetric brightness hysteresis. A frame counts toward OPENING only above OpenBrightness and
        // toward CLOSING only below CloseBrightness; readings in the [80,100] dead zone hold the current
        // state so brightness hovering at the boundary can't flicker the overlay. OpenBrightness stays
        // at the detector's old threshold (100) on purpose — real panels read as low as 101, so raising
        // it would miss dim ones; the confirm-gate (above) is what rejects bright-but-fake frames.
        const int OpenBrightness = 100;
        const int CloseBrightness = 80;

        PriceOverlayManager.EnsureVisible(_config.RegionRect, _config.OverlayXOffset, _icons);
        Log("overlay ready");

        while (!ct.IsCancellationRequested)
        {
            var cycleStart = sw.ElapsedMilliseconds;
            cycleCount++;
            try
            {
                using var bmp = _capture.CaptureRegion(_config.RegionRect);
                var sampledPixel = detector.SampleAverage(bmp);
                int brightness = (sampledPixel.R + sampledPixel.G + sampledPixel.B) / 3;
                bool brightFrame = brightness > OpenBrightness;   // strong enough to count toward opening
                bool darkFrame = brightness < CloseBrightness;    // dim enough to count toward closing

                // Dismissed (ESC / Left-Ctrl+click): stay hidden and don't scan until the panel
                // actually closes (a few genuinely dark frames). ESC closes the panel so this clears
                // quickly; Ctrl+click keeps it open, so the overlay stays dismissed (no flicker) until
                // the user closes the panel. On release, arm hint-suppression so the next brightness
                // blip can't re-show the overlay before OCR re-confirms a real panel.
                if (_dismissed)
                {
                    if (darkFrame) dismissDark++; else dismissDark = 0;
                    if (dismissDark >= DarkToRelease)
                    {
                        _dismissed = false;
                        suppressHintUntilConfirm = true;
                        Log("dismiss released (panel closed)");
                    }
                    isOpen = false; confirmedOpen = false; brightStreak = 0; darkStreak = 0;
                    ocrEmptyStreak = 0;
                    quantityMemory.Clear();
                    scrollHoldoffUntil = DateTime.MinValue;
                    slots.Clear(); lastRows = [];
                    _showing = false;
                    PriceOverlayManager.UpdateState([], false, false, null);
                }
                else
                {
                    dismissDark = 0;

                    // Hysteresis: 2 consecutive bright frames to open, 3 dark frames to close; readings
                    // in the [CloseBrightness, OpenBrightness] dead zone hold the current state.
                    if (brightFrame) { brightStreak++; darkStreak = 0; }
                    else if (darkFrame) { darkStreak++; brightStreak = 0; }
                    else { brightStreak = 0; darkStreak = 0; }
                    bool prevIsOpen = isOpen;
                    if (!isOpen && brightStreak >= 2) isOpen = true;
                    else if (isOpen && darkStreak >= 3) isOpen = false;

                    // Heartbeat every ~5s so we know the loop is alive
                    if (cycleCount % 12 == 0)
                    {
                        Log($"heartbeat cycle={cycleCount} panelOpen={isOpen} confirmed={confirmedOpen} region={_config.RegionRect} rows={lastRows.Count} " +
                            $"avgPixel=#{sampledPixel.R:X2}{sampledPixel.G:X2}{sampledPixel.B:X2} brightness={brightness}");
                    }

                    if (isOpen != prevIsOpen)
                    {
                        Log($"panel {(isOpen ? "OPEN" : "CLOSED")} brightness={brightness} " +
                            $"avgPixel=#{sampledPixel.R:X2}{sampledPixel.G:X2}{sampledPixel.B:X2}");

                        // Panel just detected — show the "reading…" hint right away, before the first
                        // (200–400ms) OCR runs, so the wait isn't a blank screen. But right after a
                        // dismiss, suppress it: a brightness blip that isn't a real panel never
                        // confirms, so showing the hint here is exactly the post-ESC flicker.
                        if (isOpen && !suppressHintUntilConfirm)
                        {
                            _showing = false;
                            PriceOverlayManager.UpdateState([], false, true);
                        }
                    }

                    if (isOpen)
                    {
                        var now = DateTime.UtcNow;
                        if ((now - lastOcrAt).TotalMilliseconds >= MinOcrIntervalMs)
                        {
                            lastOcrAt = now;
                            var ocrRows = scanner.Scan(bmp);
                            if (ocrRows.Count == 0)
                            {
                                // Panel mid-animation or a bad frame. Keep rows for one miss to avoid
                                // flicker, but clear stale prices if misses persist.
                                ocrEmptyStreak++;
                                if (ocrEmptyStreak >= ClearAfterEmptyOcr)
                                {
                                    slots.Clear();
                                    lastRows = [];
                                    confirmedOpen = false;
                                }
                                Log("OCR returned 0 rows");
                            }
                            else
                            {
                                var reads = BuildPriceRows(ocrRows);
                                Log($"OCR {ocrRows.Count} rows → " +
                                    string.Join(" | ", reads.Select(r =>
                                        $"raw='{r.OcrText.Trim()}' y={r.CenterY} " +
                                        $"{(r.HasPrice ? $"HIT→'{r.Name}'" : "MISS")}")));

                                // Confirm a real exchange panel only when OCR resolves an actual
                                // priced item — combat effects / stray windows never do.
                                if (!confirmedOpen && reads.Any(r => r.HasPrice))
                                {
                                    confirmedOpen = true;
                                    suppressHintUntilConfirm = false;
                                    Log("panel CONFIRMED (priced row found)");
                                }

                                // No priced rows in consecutive OCR passes usually means stale content;
                                // clear locked rows quickly so old prices don't linger on-screen.
                                if (reads.Any(r => r.HasPrice)) ocrEmptyStreak = 0;
                                else if (++ocrEmptyStreak >= ClearAfterEmptyOcr)
                                {
                                    slots.Clear();
                                    lastRows = [];
                                    confirmedOpen = false;
                                }

                                // Per-row slots: a row locks once confirmed, then stays fixed;
                                // unpriced rows keep being retried every pass.
                                lastRows = MergeReads(slots, reads, quantityMemory, now, out bool scrollDetected);
                                if (scrollDetected) scrollHoldoffUntil = now.AddMilliseconds(ScrollHoldoffMs);
                                if (now < scrollHoldoffUntil)
                                {
                                    lastRows = lastRows.Select(r =>
                                        r.HasPrice && r.Confidence < 0.85 && r.Multiplier <= 1 && !r.MultiplierExplicit
                                            ? r with { HasPrice = false, DivineValue = 0m, ExaltedValue = 0m }
                                            : r).ToList();
                                }
                            }
                        }
                    }
                    else
                    {
                        ocrEmptyStreak = 0;
                        quantityMemory.Clear();
                        scrollHoldoffUntil = DateTime.MinValue;
                        slots.Clear();
                        lastRows = [];
                        confirmedOpen = false;
                    }

                    // "reading" = brightness says a panel is up but OCR hasn't confirmed prices yet.
                    // Suppressed straight after a dismiss until a real confirm (anti-flicker, see above).
                    bool reading = isOpen && !confirmedOpen && !suppressHintUntilConfirm;
                    string hud = BuildDebugHud(lastRows, DateTime.UtcNow < scrollHoldoffUntil);

                    // Show prices only once OCR has confirmed a real list, not on brightness alone.
                    _showing = confirmedOpen;
                    PriceOverlayManager.UpdateState(lastRows, confirmedOpen, reading, hud);

                    topmostCounter++;
                    if (topmostCounter >= TopmostEveryN)
                    {
                        PriceOverlayManager.ForceTopmost();
                        topmostCounter = 0;
                    }
                }
            }
            catch (Exception ex)
            {
                Log($"ERROR {ex.GetType().Name}: {ex.Message}");
            }

            var cycleMs = sw.ElapsedMilliseconds - cycleStart;
            var wait = (int)Math.Max(0, (isOpen ? OpenCycleMs : ClosedCycleMs) - cycleMs);
            if (wait > 0)
            {
                try { await Task.Delay(wait, ct); }
                catch (OperationCanceledException) { break; }
            }
        }

        _showing = false;
        PriceOverlayManager.Hide();
        Log("loop exited");
    }

    private IReadOnlyList<PriceRow> BuildPriceRows(IReadOnlyList<OcrRow> ocrRows)
    {
        var snap = _prices.Current;
        var snapshot = snap.Prices;
        var rows = new List<PriceRow>(ocrRows.Count);
        var newPositions = new Dictionary<string, int>(ocrRows.Count);

        // Invalidate the resolution cache when the price snapshot changed since the last build.
        // (Gem rows below are resolved independently and are NOT cached.)
        if (_prices.PriceGeneration != _cachedPriceGeneration)
        {
            _cachedPriceGeneration = _prices.PriceGeneration;
            _resolutionCache.Clear();
        }

        foreach (var row in ocrRows)
        {
            if (row.NormalizedName.Contains("runeshape"))
                continue;

            int stableY = row.CenterY;
            if (_lastPositions.TryGetValue(row.NormalizedName, out int prevY) &&
                Math.Abs(prevY - row.CenterY) < 5)
                stableY = prevY;
            newPositions[row.NormalizedName] = stableY;

            // Uncut gems (skill / spirit / support) are priced PER LEVEL, and adjacent levels differ
            // several-fold (e.g. spirit gem L18 ≈ 0.027 div vs L19 ≈ 0.143 div). The only things that
            // distinguish one gem line from another are the TYPE word and the LEVEL number, so we pin
            // both EXACTLY and deliberately skip the prefix/fuzzy fallbacks here: a single-character OCR
            // slip on the digit (or skill↔spirit) would otherwise lock a confidently-wrong, multiples-off
            // price. If the type or level can't be read cleanly, the row shows '?' until a clean read
            // arrives — better than guessing a neighbouring level.
            if (TryResolveGemKey(row.NormalizedName, out var gemKey))
            {
                if (gemKey is not null && snapshot.TryGetValue(gemKey, out var gemEntry))
                    rows.Add(new PriceRow(stableY, row.RawText, gemEntry.DivineValue, gemEntry.ExaltedValue,
                        true, row.Multiplier, gemKey, true, MemeKind.None, row.MultiplierExplicit));
                else
                    // Recognised as an uncut gem but type+level didn't pin to a known price → '?', never fuzzy.
                    rows.Add(new PriceRow(stableY, row.RawText, 0m, 0m, false,
                        row.Multiplier, row.NormalizedName, false, MemeKind.None, row.MultiplierExplicit));
                continue;
            }

            // Easter eggs: certain OCR'd names render as a gag icon + caption instead of a price.
            // ExactMatch=true so they lock on the first read like a real priced row.
            //   "5x random currency" (the "5x" is stripped into the multiplier, leaving "random
            //    currency") → Mirror of Kalandra. "unique belt" → Headhunter.
            if (row.NormalizedName.Contains("random") && row.NormalizedName.Contains("currency"))
            {
                rows.Add(new PriceRow(stableY, row.RawText, 0m, 0m, true,
                    row.Multiplier, "random currency", true, MemeKind.Mirror, row.MultiplierExplicit));
                continue;
            }
            if (row.NormalizedName.Contains("unique") && row.NormalizedName.Contains("belt"))
            {
                rows.Add(new PriceRow(stableY, row.RawText, 0m, 0m, true,
                    row.Multiplier, "unique belt", true, MemeKind.Headhunter, row.MultiplierExplicit));
                continue;
            }

            // Resolve the OCR'd name to a price key: exact → prefix → fuzzy (edit distance).
            // The fuzzy step rescues single-character misreads ("viswn" → "vision"). The matched
            // key (not the noisy OCR text) is stored as the row Name so the same item locks even
            // when OCR jitters between passes.
            PriceEntry? entry;
            string matchedKey = row.NormalizedName;
            bool exact = false;
            if (_resolutionCache.TryGetValue(row.NormalizedName, out var cached))
            {
                // Reuse a previously resolved key (or a recorded miss). The same OCR'd names recur
                // on every pass while a panel is open, so this skips the dict scan + Levenshtein
                // work on all but the first pass. The Exact flag is preserved from the original
                // resolution so fuzzy high-confidence matches (score ≥ 0.92) still lock in 1 read
                // on subsequent passes — recalculating it as cachedKey == NormalizedName would
                // wrongly degrade them to needing 2 reads.
                if (cached.Key is not null && snapshot.TryGetValue(cached.Key, out entry))
                {
                    matchedKey = cached.Key;
                    exact = cached.Exact;
                }
                else
                {
                    entry = null;   // cached miss
                }
            }
            else
            {
                if (snapshot.TryGetValue(row.NormalizedName, out entry))
                {
                    exact = true;
                }
                else if (row.NormalizedName.Length >= 10 &&
                         snapshot.Keys.Where(k => k.StartsWith(row.NormalizedName, StringComparison.Ordinal))
                                      .MinBy(k => k.Length) is { } prefixKey)
                {
                    entry = snapshot[prefixKey];
                    matchedKey = prefixKey;
                }
                else if (row.NormalizedName.Length >= 6 &&
                         BestFuzzy(snapshot, snap.KeysByLength, row.NormalizedName) is { } fuzzy &&
                         snapshot.TryGetValue(fuzzy.Key, out entry))
                {
                    matchedKey = fuzzy.Key;
                    exact = fuzzy.Score >= HighConfidenceThreshold;
                }
                else
                {
                    entry = null;
                }
                // Cache the resolution: the matched key, or null to record a miss.
                _resolutionCache[row.NormalizedName] = entry != null ? (matchedKey, exact) : (null, false);
            }

            if (entry != null)
                rows.Add(new PriceRow(stableY, row.RawText, entry.DivineValue, entry.ExaltedValue, true,
                    row.Multiplier, matchedKey, exact, MemeKind.None, row.MultiplierExplicit));
            else
                rows.Add(new PriceRow(stableY, row.RawText, 0m, 0m, false,
                    row.Multiplier, row.NormalizedName, false, MemeKind.None, row.MultiplierExplicit));
        }
        _lastPositions = newPositions;
        return rows;
    }

    // Pre-compiled regexes for gem detection (TryResolveGemKey runs on every OCR'd line).
    private static readonly Regex GemTypePattern = new(@"\b(skill|spirit|support)\b", RegexOptions.Compiled);
    private static readonly Regex GemLevelPattern = new(@"\blevel\s+(\d+)\b", RegexOptions.Compiled);

    // Minimum character-similarity (1 - editDistance/maxLen) for a fuzzy price match.
    // 0.84 lets ~2 wrong characters through on a 12+ char name, 1 on a ~6 char name —
    // enough to absorb typical OCR slips without matching an unrelated item.
    private const double FuzzyThreshold = 0.84;
    // Fuzzy matches at or above this score are trusted as much as exact matches (lock in 1 read
    // instead of 2). At 0.92 the edit distance is ≤1 char on a 12+ char name — a false positive
    // at this level is virtually impossible.
    private const double HighConfidenceThreshold = 0.92;

    // Closest price key to an OCR'd name by Levenshtein similarity, or null if nothing clears
    // FuzzyThreshold. Only candidates within ±3 of the name's length are considered (cheaper,
    // and a large length gap is never a near-match). The length-bucketed index avoids iterating
    // every key in the snapshot — we walk only the buckets near the name's length.
    // Returns the matched key AND its similarity score so the caller can trust high-confidence
    // matches (≥ HighConfidenceThreshold) as if they were exact.
    private static (string Key, double Score)? BestFuzzy(
        IReadOnlyDictionary<string, PriceEntry> snapshot,
        IReadOnlyDictionary<int, List<string>> keysByLength,
        string name)
    {
        string? best = null;
        double bestScore = FuzzyThreshold;   // must strictly exceed the threshold to win
        // Only check keys within ±3 of the name's length, using the pre-built index.
        for (int len = Math.Max(0, name.Length - 3); len <= name.Length + 3; len++)
        {
            if (!keysByLength.TryGetValue(len, out var keys)) continue;
            foreach (var key in keys)
            {
                int dist = Levenshtein(name, key);
                double score = 1.0 - (double)dist / Math.Max(name.Length, key.Length);
                if (score > bestScore) { bestScore = score; best = key; }
            }
        }
        return best is not null ? (best, bestScore) : null;
    }

    // Detect an uncut gem and pin its identity. Returns true when the name is an uncut gem (a type
    // word skill/spirit/support together with "gem"); the discriminating type word and "gem" are what
    // mark it, so a slip in the boilerplate words ("uncot", "levei") doesn't hide a gem. When a level
    // number is also present, `key` is the canonical price key with the type and level pinned exactly
    // (no fuzzy) — caller looks it up as-is. When the level can't be read, `key` is null so the caller
    // shows '?' rather than guessing an adjacent level (which can be several-fold off).
    internal static bool TryResolveGemKey(string normalizedName, out string? key)
    {
        key = null;
        if (!normalizedName.Contains("gem")) return false;
        var type = GemTypePattern.Match(normalizedName);
        if (!type.Success) return false;
        var lvl = GemLevelPattern.Match(normalizedName);
        if (lvl.Success) key = $"uncut {type.Groups[1].Value} gem level {lvl.Groups[1].Value}";
        return true;
    }

    internal static int Levenshtein(string a, string b)
    {
        var prev = new int[b.Length + 1];
        var curr = new int[b.Length + 1];
        for (int j = 0; j <= b.Length; j++) prev[j] = j;
        for (int i = 1; i <= a.Length; i++)
        {
            curr[0] = i;
            for (int j = 1; j <= b.Length; j++)
            {
                int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                curr[j] = Math.Min(Math.Min(curr[j - 1] + 1, prev[j] + 1), prev[j - 1] + cost);
            }
            (prev, curr) = (curr, prev);
        }
        return prev[b.Length];
    }

    // One display row per screen position. A slot locks onto a price once the same item name
    // is read on two consecutive passes, then stays fixed (noise can't dislodge it). Rows that
    // are still unpriced keep showing the latest attempt and get re-read every pass, so an early
    // misread no longer freezes a row — a later correct read upgrades it.
    private sealed class RowSlot
    {
        public int Y;                    // stable display position (first-seen)
        public PriceRow Latest = null!;  // most recent read (shown, as unpriced, until locked)
        public bool Locked;              // a confirmed price is pinned
        public PriceRow LockedRow = null!;
        public string? PendingName;      // candidate price name awaiting a second confirming read
        public int PendingCount;
        public int Unseen;               // consecutive passes this slot wasn't matched
    }

    private IReadOnlyList<PriceRow> MergeReads(
        List<RowSlot> slots,
        IReadOnlyList<PriceRow> reads,
        Dictionary<string, (int Multiplier, DateTime ExpiresUtc)> quantityMemory,
        DateTime nowUtc,
        out bool scrollDetected)
    {
        const int Tolerance = 20;   // px: how far a read can move and still be the same row
        const int Confirm = 2;      // matching fuzzy/prefix reads before a row locks (exact: 1)
        const int EvictAfter = 1;   // passes a slot can go unmatched before it's dropped (faster scroll cleanup)
        const int NearDuplicateY = 18; // px: unmatched stale slot near a matched slot is removed immediately
        const int UncertainSingleConfirm = 8; // uncertain 1x reads need stability before locking/rendering
        const double RenderThreshold = 0.72;

        scrollDetected = false;

        // Trim expired quantity memory in-place.
        foreach (var key in quantityMemory.Where(kv => kv.Value.ExpiresUtc <= nowUtc)
                                          .Select(kv => kv.Key)
                                          .ToList())
            quantityMemory.Remove(key);

        static string? SlotName(RowSlot s)
        {
            if (s.Locked && !string.IsNullOrEmpty(s.LockedRow.Name)) return s.LockedRow.Name;
            if (s.Latest.HasPrice && !string.IsNullOrEmpty(s.Latest.Name)) return s.Latest.Name;
            return null;
        }

        // Panel-switch detection: the user opened a different panel without the overlay closing.
        // Locked rows are otherwise sticky (a miss never unlocks them), so they'd keep showing the
        // previous panel's prices. If two or more locked positions now read a *different* priced
        // item, the content changed — drop only the changed slots so the new panel takes over.
        // (Previously this cleared ALL slots, which was too aggressive: OCR jitter on 2 fuzzy
        // matches could trigger a false panel-switch and wipe all locking progress.)
        var changedSlots = new List<RowSlot>();
        foreach (var read in reads)
        {
            if (!read.HasPrice) continue;
            var locked = slots.FirstOrDefault(s => s.Locked && Math.Abs(s.Y - read.CenterY) <= Tolerance);
            if (locked is not null && locked.LockedRow.Name != read.Name)
                changedSlots.Add(locked);
        }
        if (changedSlots.Count >= 2)
        {
            foreach (var s in changedSlots)
                slots.Remove(s);
            Log($"panel switch detected ({changedSlots.Count} rows changed) — resetting changed slots only");
        }

        var matched = new HashSet<RowSlot>();
        int movedByScrollCount = 0;
        foreach (var read in reads)
        {
            RowSlot? slot = null;
            int best = int.MaxValue;
            bool matchedByName = false;
            bool movedByScroll = false;

            // Scroll support: if the same priced item is read at a very different Y, reuse that
            // existing slot by name and move it, instead of creating a second slot nearby.
            if (read.HasPrice && !string.IsNullOrEmpty(read.Name))
            {
                foreach (var s in slots)
                {
                    if (matched.Contains(s)) continue;
                    if (!string.Equals(SlotName(s), read.Name, StringComparison.Ordinal)) continue;
                    int d = Math.Abs(s.Y - read.CenterY);
                    if (d < best) { best = d; slot = s; }
                }
                if (slot is not null)
                {
                    matchedByName = true;
                    movedByScroll = best > Tolerance;
                    if (movedByScroll) movedByScrollCount++;
                    slot.Y = read.CenterY;
                }
            }

            foreach (var s in slots)
            {
                if (matched.Contains(s)) continue;
                int d = Math.Abs(s.Y - read.CenterY);
                if (d <= Tolerance && d < best) { best = d; slot = s; }
            }
            if (slot is null)
            {
                slot = new RowSlot { Y = read.CenterY };
                slots.Add(slot);
            }
            matched.Add(slot);
            slot.Unseen = 0;
            slot.Latest = read;

            if (read.HasPrice)
            {
                if (slot.PendingName == read.Name) slot.PendingCount++;
                else { slot.PendingName = read.Name; slot.PendingCount = 1; }

                // Exact dictionary matches are trustworthy enough to lock immediately; only the
                // uncertain fuzzy/prefix matches need a second confirming read.
                // While scrolling, re-matched items can move by >Tolerance; lock immediately so
                // refreshed prices appear without waiting an extra pass.
                int needed = read.Multiplier > 1
                    ? 1
                    : (!read.MultiplierExplicit
                        ? UncertainSingleConfirm
                        : (read.ExactMatch || (matchedByName && movedByScroll) ? 1 : Confirm));
                if (slot.PendingCount >= needed)
                {
                    if (!slot.Locked || slot.LockedRow.Name != read.Name)
                        Log($"locked y={slot.Y} '{read.Name}'");

                    // OCR can intermittently miss the leading Nx marker on stack rows.
                    // Once a locked row has seen a stack multiplier, keep it sticky if a
                    // later pass reads the same item as 1x, so the price label doesn't
                    // oscillate between unit-only and total(each).
                    int remembered = RememberedMultiplier(quantityMemory, read.Name, nowUtc);
                    int priorLockedMultiplier = slot.Locked && slot.LockedRow.Name == read.Name
                        ? slot.LockedRow.Multiplier
                        : 1;
                    int effectiveMultiplier = ResolveMultiplierForDisplay(
                        read.Multiplier,
                        read.MultiplierExplicit,
                        priorLockedMultiplier,
                        remembered);

                    bool effectiveMultiplierExplicit = read.MultiplierExplicit;
                    if (effectiveMultiplier > 1 && slot.Locked && slot.LockedRow.Name == read.Name)
                        effectiveMultiplierExplicit = slot.LockedRow.MultiplierExplicit || read.MultiplierExplicit;
                    bool usedMemory = effectiveMultiplier > 1 && read.Multiplier == 1 && !read.MultiplierExplicit && remembered > 1;

                    if (!string.IsNullOrEmpty(read.Name) && effectiveMultiplier > 1)
                        quantityMemory[read.Name] = (effectiveMultiplier, nowUtc.AddMilliseconds(1500));

                    double confidence = ScorePriceConfidence(
                        read.ExactMatch,
                        locked: true,
                        effectiveMultiplierExplicit,
                        effectiveMultiplier,
                        usedMemory);

                    slot.Locked = true;
                    slot.LockedRow = read with
                    {
                        CenterY = slot.Y,
                        Multiplier = effectiveMultiplier,
                        MultiplierExplicit = effectiveMultiplierExplicit,
                        Confidence = confidence,
                    };
                }
            }
            // A miss (read.HasPrice == false) does NOT reset the pending streak. A miss means
            // OCR couldn't resolve the name this pass — it's "no information", not "different item".
            // The streak resets only when a DIFFERENT priced name arrives (handled in the if-branch
            // above via PendingName comparison). Resetting on misses made fuzzy/prefix items un-
            // lockable whenever OCR alternated between a correct read and a fragmented read.
        }

        // Scroll-motion mode: when multiple rows are re-matched by name but shifted by more than
        // the normal positional tolerance in the same pass, treat it as an active scroll event and
        // drop all unmatched stale slots right away.
        if (movedByScrollCount >= 2)
        {
            scrollDetected = true;
            for (int i = slots.Count - 1; i >= 0; i--)
                if (!matched.Contains(slots[i])) slots.RemoveAt(i);
        }

        // If a name was matched this pass, immediately drop any unmatched stale slot carrying the
        // same name. This prevents the "double price, a few pixels apart" artifact while scrolling.
        var liveNames = new HashSet<string>(
            matched.Select(SlotName)
                   .OfType<string>()
                   .Where(n => n.Length > 0),
            StringComparer.Ordinal);
        for (int i = slots.Count - 1; i >= 0; i--)
        {
            if (matched.Contains(slots[i])) continue;
            var n = SlotName(slots[i]);
            if (n is not null && liveNames.Contains(n)) slots.RemoveAt(i);
        }

        // Position de-dup for scroll jitter: if an unmatched stale slot sits close to any matched
        // slot, drop it immediately (prevents two prices a few pixels apart for one visible row).
        for (int i = slots.Count - 1; i >= 0; i--)
        {
            var s = slots[i];
            if (matched.Contains(s)) continue;
            bool nearLive = matched.Any(m => Math.Abs(m.Y - s.Y) <= NearDuplicateY);
            if (nearLive) slots.RemoveAt(i);
        }

        for (int i = slots.Count - 1; i >= 0; i--)
        {
            if (matched.Contains(slots[i])) continue;
            if (++slots[i].Unseen > EvictAfter) slots.RemoveAt(i);
        }

        var display = new List<PriceRow>(slots.Count);
        foreach (var s in slots.OrderBy(s => s.Y))
        {
            PriceRow candidate;
            if (s.Locked)
            {
                candidate = s.LockedRow;
            }
            else if (s.Latest.HasPrice && (s.Latest.Multiplier > 1 || s.Latest.MultiplierExplicit))
            {
                int remembered = RememberedMultiplier(quantityMemory, s.Latest.Name, nowUtc);
                int effectiveMultiplier = ResolveMultiplierForDisplay(
                    s.Latest.Multiplier,
                    s.Latest.MultiplierExplicit,
                    priorLockedMultiplier: 1,
                    rememberedMultiplier: remembered);
                bool usedMemory = effectiveMultiplier > 1 && s.Latest.Multiplier == 1 && !s.Latest.MultiplierExplicit && remembered > 1;
                bool explicitQty = s.Latest.MultiplierExplicit || (usedMemory && remembered > 1);
                candidate = s.Latest with
                {
                    CenterY = s.Y,
                    Multiplier = effectiveMultiplier,
                    MultiplierExplicit = explicitQty,
                    Confidence = ScorePriceConfidence(
                        s.Latest.ExactMatch,
                        locked: false,
                        explicitQty,
                        effectiveMultiplier,
                        usedMemory),
                };
            }
            else
            {
                candidate = s.Latest with { CenterY = s.Y, HasPrice = false, DivineValue = 0m, ExaltedValue = 0m, Confidence = 0.0 };
            }

            if (candidate.HasPrice && candidate.Confidence < RenderThreshold)
                candidate = candidate with { HasPrice = false, DivineValue = 0m, ExaltedValue = 0m };

            display.Add(candidate);
        }
        return display;
    }

    internal static int ResolveMultiplierForDisplay(
        int readMultiplier,
        bool readMultiplierExplicit,
        int priorLockedMultiplier,
        int rememberedMultiplier)
    {
        if (readMultiplier > 1) return readMultiplier;
        if (priorLockedMultiplier > 1 && readMultiplier == 1) return priorLockedMultiplier;
        if (!readMultiplierExplicit && rememberedMultiplier > 1) return rememberedMultiplier;
        return readMultiplier;
    }

    internal static double ScorePriceConfidence(
        bool exactMatch,
        bool locked,
        bool multiplierExplicit,
        int multiplier,
        bool usedMemory)
    {
        double score = locked ? 0.82 : 0.62;
        if (exactMatch) score += 0.12;
        if (multiplier > 1 && multiplierExplicit) score += 0.12;
        if (usedMemory) score += 0.06;
        if (multiplier == 1 && !multiplierExplicit) score -= 0.18;
        return Math.Clamp(score, 0.0, 1.0);
    }

    private static int RememberedMultiplier(
        Dictionary<string, (int Multiplier, DateTime ExpiresUtc)> quantityMemory,
        string name,
        DateTime nowUtc)
    {
        if (string.IsNullOrEmpty(name)) return 1;
        if (!quantityMemory.TryGetValue(name, out var m)) return 1;
        if (m.ExpiresUtc <= nowUtc) return 1;
        return Math.Max(1, m.Multiplier);
    }

    private static string BuildDebugHud(IReadOnlyList<PriceRow> rows, bool scrollHoldoffActive)
    {
        int priced = rows.Count(r => r.HasPrice);
        int explicitQty = rows.Count(r => r.HasPrice && r.MultiplierExplicit);
        int uncertain = rows.Count(r => r.HasPrice && !r.MultiplierExplicit);
        return $"rows={rows.Count} priced={priced} qty-exp={explicitQty} qty-unc={uncertain} scroll={(scrollHoldoffActive ? "ON" : "off")}";
    }

    public void Dispose()
    {
        StopAndWait(TimeSpan.FromSeconds(2));
        _cts?.Dispose();
        _capture.Dispose();
    }
}

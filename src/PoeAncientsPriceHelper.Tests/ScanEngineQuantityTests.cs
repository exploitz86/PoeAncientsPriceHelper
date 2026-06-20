using PoeAncientsPriceHelper;

namespace PoeAncientsPriceHelper.Tests;

public class ScanEngineQuantityTests
{
    [Theory]
    [InlineData(3, true, 1, 1, 3)]
    [InlineData(1, false, 3, 1, 3)]
    [InlineData(1, false, 1, 3, 3)]
    [InlineData(1, true, 1, 3, 1)]
    [InlineData(1, false, 1, 1, 1)]
    public void ResolveMultiplierForDisplay_PrefersReliableStackSignal(
        int readMultiplier,
        bool readExplicit,
        int priorLocked,
        int remembered,
        int expected)
    {
        int actual = ScanEngine.ResolveMultiplierForDisplay(readMultiplier, readExplicit, priorLocked, remembered);
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void ScorePriceConfidence_PenalizesUncertainSingleAndRewardsLockedExplicit()
    {
        double uncertainSingle = ScanEngine.ScorePriceConfidence(
            exactMatch: false,
            locked: false,
            multiplierExplicit: false,
            multiplier: 1,
            usedMemory: false);

        double lockedExplicitStack = ScanEngine.ScorePriceConfidence(
            exactMatch: true,
            locked: true,
            multiplierExplicit: true,
            multiplier: 3,
            usedMemory: false);

        Assert.True(lockedExplicitStack > uncertainSingle);
        Assert.InRange(lockedExplicitStack, 0.9, 1.0);
        Assert.InRange(uncertainSingle, 0.0, 0.7);
    }
}

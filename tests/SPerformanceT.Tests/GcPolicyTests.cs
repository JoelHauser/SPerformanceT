using SPerformanceT.Affinity;
using SPerformanceT.GarbageCollection;

namespace SPerformanceT.Tests;

public class GcPolicyTests
{
    private const long Gb = 1024L * 1024 * 1024;

    [Fact]
    public void NothingHappensWithRoomToSpare()
    {
        Assert.Equal(SafetyReason.None, GcPolicy.Check(2 * Gb, 20 * Gb, 6, 3));
    }

    [Fact]
    public void HeapAtTheLimitTriggers()
    {
        Assert.Equal(SafetyReason.HeapLimit, GcPolicy.Check(6 * Gb, 20 * Gb, 6, 3));
    }

    [Fact]
    public void LowFreeRamTriggers()
    {
        Assert.Equal(SafetyReason.LowFreeRam, GcPolicy.Check(2 * Gb, 2 * Gb, 6, 3));
    }

    [Fact]
    public void HeapLimitWinsWhenBothTrip()
    {
        Assert.Equal(SafetyReason.HeapLimit, GcPolicy.Check(7 * Gb, 1 * Gb, 6, 3));
    }

    [Fact]
    public void UnknownFreeRamIsNotATrigger()
    {
        // FreeRamBytes returns -1 when GlobalMemoryStatusEx fails.
        Assert.Equal(SafetyReason.None, GcPolicy.Check(2 * Gb, -1, 6, 3));
    }

    [Fact]
    public void AHeapCleanupThatFreesEnoughTurnsGcOffAgain()
    {
        Assert.True(GcPolicy.SafeToTurnOffAgain(SafetyReason.HeapLimit, 6 * Gb, 2 * Gb, 6));
    }

    [Fact]
    public void AHeapThatStaysNearTheLimitKeepsGcOn()
    {
        // 4.6 GB is above 75% of 6 GB (4.5 GB): mostly live data, so it would trip again at once.
        Assert.False(GcPolicy.SafeToTurnOffAgain(SafetyReason.HeapLimit, 6 * Gb, (long)(4.6 * Gb), 6));
    }

    [Fact]
    public void LowRamCleanupNeedsToFindRealGarbage()
    {
        Assert.True(GcPolicy.SafeToTurnOffAgain(SafetyReason.LowFreeRam, 4 * Gb, 2 * Gb, 6));
        Assert.False(GcPolicy.SafeToTurnOffAgain(SafetyReason.LowFreeRam, 4 * Gb, (long)(3.5 * Gb), 6));
    }

    [Theory]
    [InlineData(2f, 2_000_000UL)]
    [InlineData(0.5f, 500_000UL)]
    [InlineData(10f, 10_000_000UL)]
    [InlineData(0f, 500_000UL)]
    [InlineData(-3f, 500_000UL)]
    [InlineData(float.NaN, 500_000UL)]
    [InlineData(500f, 50_000_000UL)]
    public void SlicesConvertToNanoseconds(float ms, ulong expected)
    {
        Assert.Equal(expected, GcPolicy.SliceNanoseconds(ms));
    }

    [Fact]
    public void GigabytesFormatTheSameInEveryCulture()
    {
        var saved = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
            Assert.Equal("1.50 GB", GcPolicy.Gb(Gb + Gb / 2));
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = saved;
        }
    }

    [Theory]
    [InlineData(0xFFFFUL, 15)]   // 12700K P-cores
    [InlineData(0x5555UL, 7)]    // one thread per P-core
    [InlineData(0xFFFFFUL, 19)]  // all 20 threads, Unity's own default
    [InlineData(0x1UL, 1)]       // never below one
    [InlineData(0x3UL, 1)]
    public void WorkersAreThePinnedThreadsMinusTheMainThread(ulong mask, int expected)
    {
        Assert.Equal(expected, AffinityPlan.WorkerCount(mask));
    }
}

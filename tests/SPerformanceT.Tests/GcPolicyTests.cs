using SPerformanceT.Affinity;
using SPerformanceT.GarbageCollection;

namespace SPerformanceT.Tests;

public class GcPolicyTests
{
    private const long Gb = 1024L * 1024 * 1024;

    // Defaults: heap limit 6 GB, cleanup every 1 GB of growth, emergency below 2 GB free commit.
    private static SafetyReason Check(double heapGb, double baselineGb, double freeCommitGb) =>
        GcPolicy.Check((long)(heapGb * Gb), (long)(baselineGb * Gb), (long)(freeCommitGb * Gb), 6, 1, 2);

    [Fact]
    public void NothingHappensWithRoomToSpare()
    {
        Assert.Equal(SafetyReason.None, Check(2.5, 2, 10));
    }

    [Fact]
    public void LowFreePhysicalRamIsNotATrigger()
    {
        // The 2026-10-02 Streets raid: 3.1 GB heap, barely grown, 2.9 GB free RAM, ~6.5 GB free commit.
        // 0.3.0 cleaned up on low free RAM, freed 0.23 GB and fell back to automatic GC for nothing.
        Assert.Equal(SafetyReason.None, Check(3.11, 2.83, 6.5));
    }

    [Fact]
    public void HeapGrowthTriggers()
    {
        Assert.Equal(SafetyReason.HeapGrowth, Check(3.9, 2.9, 10));
        Assert.Equal(SafetyReason.None, Check(3.8, 2.9, 10));
    }

    [Fact]
    public void HeapAtTheLimitTriggers()
    {
        Assert.Equal(SafetyReason.HeapLimit, Check(6, 5.5, 10));
    }

    [Fact]
    public void LowCommitTriggersFirst()
    {
        Assert.Equal(SafetyReason.LowCommit, Check(7, 2, 1.5));
    }

    [Fact]
    public void UnknownCommitIsNotATrigger()
    {
        // ReadMemory reports -1 when GlobalMemoryStatusEx fails.
        Assert.Equal(SafetyReason.None, GcPolicy.Check(2 * Gb, 2 * Gb, -1, 6, 1, 2));
    }

    [Fact]
    public void InventoryAndGrowthCleanupsAlwaysTurnGcOffAgain()
    {
        Assert.True(GcPolicy.SafeToTurnOffAgain(SafetyReason.None, 5 * Gb, 6));
        Assert.True(GcPolicy.SafeToTurnOffAgain(SafetyReason.HeapGrowth, 5 * Gb, 6));
    }

    [Fact]
    public void AHeapLimitCleanupThatFreesEnoughTurnsGcOffAgain()
    {
        Assert.True(GcPolicy.SafeToTurnOffAgain(SafetyReason.HeapLimit, 2 * Gb, 6));
    }

    [Fact]
    public void AHeapThatStaysNearTheLimitKeepsGcOn()
    {
        // 4.6 GB is above 75% of 6 GB (4.5 GB): mostly live data, so it would trip again at once.
        Assert.False(GcPolicy.SafeToTurnOffAgain(SafetyReason.HeapLimit, (long)(4.6 * Gb), 6));
    }

    [Fact]
    public void LowCommitAlwaysKeepsGcOn()
    {
        Assert.False(GcPolicy.SafeToTurnOffAgain(SafetyReason.LowCommit, 1 * Gb, 6));
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

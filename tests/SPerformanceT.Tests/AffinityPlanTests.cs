using SPerformanceT.Affinity;

namespace SPerformanceT.Tests;

public class AffinityPlanTests
{
    /// <summary>
    /// What GetSystemCpuSetInformation reported on Joel's 12700K under Windows 10 on 2026-10-02:
    /// threads 0-15 in pairs on cores 0,2,..,14 at class 1; threads 16-19 on cores 16-19 at class 0.
    /// </summary>
    private static List<LogicalCpu> I7_12700K()
    {
        var cpus = new List<LogicalCpu>();
        for (int i = 0; i < 16; i++)
            cpus.Add(new LogicalCpu(0, i, i & ~1, 1));
        for (int i = 16; i < 20; i++)
            cpus.Add(new LogicalCpu(0, i, i, 0));
        return cpus;
    }

    private const ulong System12700K = 0xFFFFF;

    /// <summary>8 P-cores with hyperthreading, 16 E-cores: 32 threads.</summary>
    private static List<LogicalCpu> I9_13900K()
    {
        var cpus = new List<LogicalCpu>();
        for (int i = 0; i < 16; i++)
            cpus.Add(new LogicalCpu(0, i, i & ~1, 1));
        for (int i = 16; i < 32; i++)
            cpus.Add(new LogicalCpu(0, i, i, 0));
        return cpus;
    }

    /// <summary>A Ryzen-style part: 8 cores, 16 threads, one class.</summary>
    private static List<LogicalCpu> EightCoreNonHybrid()
    {
        var cpus = new List<LogicalCpu>();
        for (int i = 0; i < 16; i++)
            cpus.Add(new LogicalCpu(0, i, i & ~1, 0));
        return cpus;
    }

    [Fact]
    public void The12700KSplitsIntoPAndECores()
    {
        var cpus = I7_12700K();
        Assert.True(AffinityPlan.IsHybrid(cpus));
        Assert.Equal(0xFFFFUL, AffinityPlan.PerformanceMask(cpus, false));
        Assert.Equal(0x5555UL, AffinityPlan.PerformanceMask(cpus, true));
        Assert.Equal(0xF0000UL, AffinityPlan.EfficiencyMask(cpus));
    }

    [Fact]
    public void The13900KSplitsIntoPAndECores()
    {
        var cpus = I9_13900K();
        Assert.Equal(0xFFFFUL, AffinityPlan.PerformanceMask(cpus, false));
        Assert.Equal(0xFFFF0000UL, AffinityPlan.EfficiencyMask(cpus));
    }

    [Fact]
    public void DefaultModePinsGameToPCoresAndServerToECores()
    {
        Plan plan = AffinityPlan.Build(I7_12700K(), System12700K, AffinityMode.PerformanceCores, "0xFFFF", true);
        Assert.Equal(0xFFFFUL, plan.GameMask);
        Assert.Equal(0xF0000UL, plan.ServerMask);
        Assert.Null(plan.Problem);
    }

    [Fact]
    public void NoHyperthreadingModeTakesOneThreadPerPCore()
    {
        Plan plan = AffinityPlan.Build(I7_12700K(), System12700K, AffinityMode.PerformanceCoresNoHyperthreading, "", false);
        Assert.Equal(0x5555UL, plan.GameMask);
    }

    [Fact]
    public void ServerIsLeftAloneWhenNotAsked()
    {
        Plan plan = AffinityPlan.Build(I7_12700K(), System12700K, AffinityMode.PerformanceCores, "", false);
        Assert.Equal(0UL, plan.ServerMask);
    }

    [Fact]
    public void OffTouchesNothing()
    {
        Plan plan = AffinityPlan.Build(I7_12700K(), System12700K, AffinityMode.Off, "0xFF", true);
        Assert.Equal(0UL, plan.GameMask);
        Assert.Equal(0UL, plan.ServerMask);
    }

    [Fact]
    public void ANonHybridCpuIsLeftAlone()
    {
        var cpus = EightCoreNonHybrid();
        Assert.False(AffinityPlan.IsHybrid(cpus));
        Plan plan = AffinityPlan.Build(cpus, 0xFFFF, AffinityMode.PerformanceCores, "", true);
        Assert.Equal(0UL, plan.GameMask);
        Assert.Equal(0UL, plan.ServerMask);
        Assert.Equal(0UL, AffinityPlan.EfficiencyMask(cpus));
    }

    [Fact]
    public void CustomMaskIsUsedAsGiven()
    {
        Plan plan = AffinityPlan.Build(I7_12700K(), System12700K, AffinityMode.Custom, "0x00FF", true);
        Assert.Equal(0xFFUL, plan.GameMask);
        Assert.Equal(0xF0000UL, plan.ServerMask);
        Assert.Null(plan.Problem);
    }

    [Fact]
    public void CustomMaskIsClippedToProcessorsThatExist()
    {
        Plan plan = AffinityPlan.Build(I7_12700K(), System12700K, AffinityMode.Custom, "0xFFFFFF", false);
        Assert.Equal(System12700K, plan.GameMask);
        Assert.NotNull(plan.Problem);
    }

    [Theory]
    [InlineData("nonsense")]
    [InlineData("")]
    [InlineData("0x")]
    [InlineData("0xF00000")] // only CPUs 20-23, none of which exist
    public void AnUnusableCustomMaskLeavesTheGameAlone(string mask)
    {
        Plan plan = AffinityPlan.Build(I7_12700K(), System12700K, AffinityMode.Custom, mask, false);
        Assert.Equal(0UL, plan.GameMask);
        Assert.NotNull(plan.Problem);
    }

    [Theory]
    [InlineData("0xFFFF", 0xFFFFUL)]
    [InlineData("FFFF", 0xFFFFUL)]
    [InlineData("ffff", 0xFFFFUL)]
    [InlineData("  0X0F0000 ", 0xF0000UL)]
    public void MasksParse(string text, ulong expected)
    {
        Assert.True(AffinityPlan.TryParseMask(text, out ulong mask));
        Assert.Equal(expected, mask);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("xyz")]
    [InlineData("0x1FFFFFFFFFFFFFFFF")] // 65 bits
    [InlineData("-1")]
    public void BadMasksDoNotParse(string text)
    {
        Assert.False(AffinityPlan.TryParseMask(text, out _));
    }

    [Theory]
    [InlineData(0xFFFFUL, "0-15")]
    [InlineData(0xF0000UL, "16-19")]
    [InlineData(0x5555UL, "0,2,4,6,8,10,12,14")]
    [InlineData(0b1101UL, "0,2-3")]
    [InlineData(0UL, "none")]
    [InlineData(0x8000000000000000UL, "63")]
    public void MasksDescribeAsRanges(ulong mask, string expected)
    {
        Assert.Equal(expected, AffinityPlan.Describe(mask));
    }

    [Fact]
    public void TopologyLineNamesBothKinds()
    {
        Assert.Equal("8 P-cores (threads 0-15), 4 E-cores (threads 16-19)",
            AffinityPlan.DescribeTopology(I7_12700K()));
        Assert.Equal("16 threads, all the same kind of core (no E-cores)",
            AffinityPlan.DescribeTopology(EightCoreNonHybrid()));
    }

    [Fact]
    public void ProcessorsOutsideGroupZeroAreIgnored()
    {
        var cpus = I7_12700K();
        cpus.Add(new LogicalCpu(1, 0, 0, 1));
        cpus.Add(new LogicalCpu(1, 1, 1, 0));
        Assert.Equal(0xFFFFUL, AffinityPlan.PerformanceMask(cpus, false));
        Assert.Equal(0xF0000UL, AffinityPlan.EfficiencyMask(cpus));
    }
}

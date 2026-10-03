using SPerformanceT.Affinity;

namespace SPerformanceT.Tests;

/// <summary>
/// What automatic pinning does on CPUs other than the 12700K it was built on. The layouts are
/// modelled from each part's core counts; only the 12700K's was read off real hardware.
/// </summary>
public class CpuCompatibilityTests
{
    private static ulong All(List<LogicalCpu> cpus) => cpus.Aggregate(0UL, (m, c) => m | 1UL << c.Index);

    /// <summary>P-cores with hyperthreading first (two threads per core), then single-thread E-cores.</summary>
    private static List<LogicalCpu> HtPThenE(int pCores, int eCores, int pClass = 1, int eClass = 0)
    {
        var cpus = new List<LogicalCpu>();
        int i = 0;
        for (int c = 0; c < pCores; c++)
        {
            cpus.Add(new LogicalCpu(0, i, i, pClass));
            cpus.Add(new LogicalCpu(0, i + 1, i, pClass));
            i += 2;
        }
        for (int c = 0; c < eCores; c++, i++)
            cpus.Add(new LogicalCpu(0, i, i, eClass));
        return cpus;
    }

    [Fact]
    public void I5_12600K_IsPinned()
    {
        // 6 P-cores with HT (12 threads) + 4 E-cores: exactly the minimum.
        var cpus = HtPThenE(6, 4);
        Plan plan = AffinityPlan.Build(cpus, All(cpus), AffinityMode.PerformanceCores, "", true);
        Assert.Equal(0xFFFUL, plan.GameMask);
        Assert.Equal(0xF000UL, plan.ServerMask);
        Assert.Null(plan.Note);
    }

    [Fact]
    public void CoreUltra9_285K_IsLeftAlone()
    {
        // Arrow Lake: 8 P-cores without hyperthreading + 16 E-cores, and the P-cores are not
        // numbered first. Pinning would put the game on 8 threads and idle 16 strong E-cores.
        var cpus = new List<LogicalCpu>();
        var pIndices = new HashSet<int> { 0, 1, 6, 7, 12, 13, 18, 19 };
        for (int i = 0; i < 24; i++)
            cpus.Add(new LogicalCpu(0, i, i, pIndices.Contains(i) ? 1 : 0));

        Assert.Equal(8, AffinityPlan.CountBits(AffinityPlan.PerformanceMask(cpus, false)));
        Plan plan = AffinityPlan.Build(cpus, All(cpus), AffinityMode.PerformanceCores, "", true);
        Assert.Equal(0UL, plan.GameMask);
        Assert.Equal(0UL, plan.ServerMask);
        Assert.Contains("Only 8 P-core threads", plan.Note);
    }

    [Fact]
    public void I7_1260P_LaptopIsLeftAlone()
    {
        // 4 P-cores with HT (8 threads) + 8 E-cores.
        var cpus = HtPThenE(4, 8);
        Plan plan = AffinityPlan.Build(cpus, All(cpus), AffinityMode.PerformanceCores, "", true);
        Assert.Equal(0UL, plan.GameMask);
        Assert.Equal(0UL, plan.ServerMask);
        Assert.NotNull(plan.Note);
    }

    [Fact]
    public void LunarLakeIsLeftAlone()
    {
        // 4 P-cores without HT + 4 low-power E-cores.
        var cpus = new List<LogicalCpu>();
        for (int i = 0; i < 8; i++)
            cpus.Add(new LogicalCpu(0, i, i, i < 4 ? 1 : 0));
        Plan plan = AffinityPlan.Build(cpus, All(cpus), AffinityMode.PerformanceCores, "", true);
        Assert.Equal(0UL, plan.GameMask);
    }

    [Fact]
    public void CoreUltra7_155H_ThreeClassesPinsPAndGivesTheServerBothSlowerKinds()
    {
        // Meteor Lake: 6 P-cores with HT (class 2), 8 E-cores (class 1), 2 low-power E-cores (class 0).
        var cpus = HtPThenE(6, 8, pClass: 2, eClass: 1);
        cpus.Add(new LogicalCpu(0, 20, 20, 0));
        cpus.Add(new LogicalCpu(0, 21, 21, 0));
        Plan plan = AffinityPlan.Build(cpus, All(cpus), AffinityMode.PerformanceCores, "", true);
        Assert.Equal(0xFFFUL, plan.GameMask);
        Assert.Equal(0x3FF000UL, plan.ServerMask);
    }

    [Fact]
    public void CustomModeStillPinsWhenAutomaticWouldNot()
    {
        var cpus = HtPThenE(4, 8);
        Plan plan = AffinityPlan.Build(cpus, All(cpus), AffinityMode.Custom, "0xFF", true);
        Assert.Equal(0xFFUL, plan.GameMask);
    }

    [Fact]
    public void NoHyperthreadingModeIsJudgedOnTheThreadsTheCpuHas()
    {
        // The minimum applies to the P-core threads available (16 on a 12700K), not to the 8 this
        // mode chooses, so it stays usable as an experiment.
        var cpus = HtPThenE(8, 4);
        Plan plan = AffinityPlan.Build(cpus, All(cpus), AffinityMode.PerformanceCoresNoHyperthreading, "", false);
        Assert.Equal(0x5555UL, plan.GameMask);
    }

    [Fact]
    public void NonHybridCpusGetTheNoECoresNote()
    {
        var cpus = new List<LogicalCpu>();
        for (int i = 0; i < 16; i++)
            cpus.Add(new LogicalCpu(0, i, i & ~1, 0));
        Plan plan = AffinityPlan.Build(cpus, All(cpus), AffinityMode.PerformanceCores, "", true);
        Assert.Equal(0UL, plan.GameMask);
        Assert.StartsWith("No E-cores", plan.Note);
    }
}

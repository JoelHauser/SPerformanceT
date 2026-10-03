using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace SPerformanceT.Affinity
{
    /// <summary>One logical processor, as Windows reports it in GetSystemCpuSetInformation.</summary>
    public readonly struct LogicalCpu
    {
        public LogicalCpu(int group, int index, int core, int efficiencyClass)
        {
            Group = group;
            Index = index;
            Core = core;
            EfficiencyClass = efficiencyClass;
        }

        /// <summary>Processor group. Affinity masks only ever cover group 0 here.</summary>
        public int Group { get; }

        /// <summary>LogicalProcessorIndex: the bit this thread is in an affinity mask.</summary>
        public int Index { get; }

        /// <summary>CoreIndex. Both threads of a hyperthreaded core share it.</summary>
        public int Core { get; }

        /// <summary>Higher is faster. Alder Lake on Windows 10 reports P-cores as 1, E-cores as 0.</summary>
        public int EfficiencyClass { get; }
    }

    public enum AffinityMode
    {
        PerformanceCores,
        PerformanceCoresNoHyperthreading,
        Custom,
        Off,
    }

    /// <summary>What to set. A mask of 0 means "leave that process alone".</summary>
    public sealed class Plan
    {
        public ulong GameMask;
        public ulong ServerMask;

        /// <summary>Something the user should hear about, such as an unusable custom mask. Null if none.</summary>
        public string Problem;

        /// <summary>Why the automatic modes chose to do nothing on this CPU. Informational. Null if none.</summary>
        public string Note;
    }

    /// <summary>
    /// Every decision the mod makes, with no game, Unity or Windows dependency, so the tests can
    /// link this file directly. Keep it that way.
    /// </summary>
    public static class AffinityPlan
    {
        public static bool IsHybrid(IList<LogicalCpu> cpus)
        {
            int min = int.MaxValue, max = int.MinValue;
            foreach (LogicalCpu cpu in Group0(cpus))
            {
                min = Math.Min(min, cpu.EfficiencyClass);
                max = Math.Max(max, cpu.EfficiencyClass);
            }
            return min < max;
        }

        /// <summary>
        /// The fastest efficiency class present. With <paramref name="oneThreadPerCore"/>, only the
        /// lowest numbered thread of each core.
        /// </summary>
        public static ulong PerformanceMask(IList<LogicalCpu> cpus, bool oneThreadPerCore)
        {
            int top = TopClass(cpus);
            var coresSeen = new HashSet<int>();
            var chosen = new List<LogicalCpu>();
            foreach (LogicalCpu cpu in Group0(cpus))
            {
                if (cpu.EfficiencyClass == top)
                    chosen.Add(cpu);
            }
            chosen.Sort((a, b) => a.Index.CompareTo(b.Index));

            ulong mask = 0;
            foreach (LogicalCpu cpu in chosen)
            {
                if (oneThreadPerCore && !coresSeen.Add(cpu.Core))
                    continue;
                mask |= Bit(cpu.Index);
            }
            return mask;
        }

        /// <summary>Everything slower than the fastest class. 0 on a CPU without E-cores.</summary>
        public static ulong EfficiencyMask(IList<LogicalCpu> cpus)
        {
            int top = TopClass(cpus);
            ulong mask = 0;
            foreach (LogicalCpu cpu in Group0(cpus))
            {
                if (cpu.EfficiencyClass < top)
                    mask |= Bit(cpu.Index);
            }
            return mask;
        }

        public static Plan Build(
            IList<LogicalCpu> cpus, ulong systemMask, AffinityMode mode, string customMask, bool pinServer)
        {
            var plan = new Plan();
            if (mode == AffinityMode.Off)
                return plan;

            bool hybrid = IsHybrid(cpus);

            if (mode == AffinityMode.Custom)
            {
                if (!TryParseMask(customMask, out ulong parsed))
                {
                    plan.Problem = "Custom mask '" + customMask + "' is not a hex mask such as 0xFFFF; "
                                   + "leaving the game's cores alone.";
                }
                else if ((parsed & systemMask) == 0)
                {
                    plan.Problem = "Custom mask " + Hex(parsed) + " names no processor this machine has ("
                                   + Hex(systemMask) + "); leaving the game's cores alone.";
                }
                else
                {
                    plan.GameMask = parsed & systemMask;
                    if (plan.GameMask != parsed)
                    {
                        plan.Problem = "Custom mask " + Hex(parsed) + " includes processors this machine "
                                       + "does not have; using " + Hex(plan.GameMask) + ".";
                    }
                }
            }
            else if (!hybrid)
            {
                plan.Note = "No E-cores on this CPU, so there is nothing to pin. Doing nothing.";
            }
            else
            {
                int pThreads = CountBits(PerformanceMask(cpus, false));
                if (pThreads < MinPerformanceThreads)
                {
                    // Core Ultra 200 (8 P-cores, no hyperthreading), Lunar Lake, hybrid laptop parts:
                    // the E-cores carry real game work there, so pinning would starve the game.
                    plan.Note = "Only " + pThreads + " P-core threads on this CPU (pinning needs at least "
                                + MinPerformanceThreads + "), so the E-cores are left to the game. Doing nothing. "
                                + "Mode Custom still pins on request.";
                    return plan;
                }
                bool oneEach = mode == AffinityMode.PerformanceCoresNoHyperthreading;
                plan.GameMask = PerformanceMask(cpus, oneEach) & systemMask;
            }

            if (pinServer && hybrid)
                plan.ServerMask = EfficiencyMask(cpus) & systemMask;

            return plan;
        }

        /// <summary>Accepts "0xFFFF", "FFFF" or "ffff", with surrounding spaces.</summary>
        public static bool TryParseMask(string text, out ulong mask)
        {
            mask = 0;
            if (text == null)
                return false;
            string s = text.Trim();
            if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                s = s.Substring(2);
            if (s.Length == 0)
                return false;
            return ulong.TryParse(s, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out mask);
        }

        /// <summary>
        /// Unity job workers for a mask: one per allowed thread, minus the main thread, at least one.
        /// Unity sizes them from every logical CPU at startup (boot.config job-worker-count=19 on a
        /// 20-thread CPU), so once the game is limited to 16 threads, 19 workers plus the main thread
        /// compete for 16.
        /// </summary>
        public static int WorkerCount(ulong mask) => Math.Max(1, CountBits(mask) - 1);

        /// <summary>
        /// Fewest P-core threads automatic pinning will accept. 12 covers Intel 12th-14th gen desktop
        /// (i5-12600K and up) and H-series laptops (12700H), the CPUs this was built for. Below it are
        /// CPUs whose E-cores the game needs: Core Ultra 200 (8 threads, no hyperthreading), Lunar
        /// Lake, the P-series laptops.
        /// </summary>
        public const int MinPerformanceThreads = 12;

        public static int CountBits(ulong mask)
        {
            int n = 0;
            for (ulong m = mask; m != 0; m &= m - 1)
                n++;
            return n;
        }

        public static string Hex(ulong mask) => "0x" + mask.ToString("X", CultureInfo.InvariantCulture);

        /// <summary>The set bits as ranges, the way Task Manager would list them: "0-15", "0,2,4".</summary>
        public static string Describe(ulong mask)
        {
            if (mask == 0)
                return "none";

            var sb = new StringBuilder();
            int i = 0;
            while (i < 64)
            {
                if ((mask & Bit(i)) == 0)
                {
                    i++;
                    continue;
                }
                int start = i;
                while (i + 1 < 64 && (mask & Bit(i + 1)) != 0)
                    i++;
                if (sb.Length > 0)
                    sb.Append(',');
                sb.Append(start);
                if (i > start)
                    sb.Append('-').Append(i);
                i++;
            }
            return sb.ToString();
        }

        /// <summary>One line for the log: what kind of cores this machine has, and where.</summary>
        public static string DescribeTopology(IList<LogicalCpu> cpus)
        {
            if (!IsHybrid(cpus))
            {
                int threads = 0;
                foreach (LogicalCpu _ in Group0(cpus))
                    threads++;
                return threads + " threads, all the same kind of core (no E-cores)";
            }

            ulong p = PerformanceMask(cpus, false);
            ulong e = EfficiencyMask(cpus);
            return CountCores(cpus, p) + " P-cores (threads " + Describe(p) + "), "
                   + CountCores(cpus, e) + " E-cores (threads " + Describe(e) + ")";
        }

        private static int CountCores(IList<LogicalCpu> cpus, ulong mask)
        {
            var cores = new HashSet<int>();
            foreach (LogicalCpu cpu in Group0(cpus))
            {
                if ((mask & Bit(cpu.Index)) != 0)
                    cores.Add(cpu.Core);
            }
            return cores.Count;
        }

        private static int TopClass(IList<LogicalCpu> cpus)
        {
            int top = int.MinValue;
            foreach (LogicalCpu cpu in Group0(cpus))
                top = Math.Max(top, cpu.EfficiencyClass);
            return top;
        }

        private static IEnumerable<LogicalCpu> Group0(IList<LogicalCpu> cpus)
        {
            foreach (LogicalCpu cpu in cpus)
            {
                if (cpu.Group == 0 && cpu.Index < 64)
                    yield return cpu;
            }
        }

        private static ulong Bit(int index) => 1UL << index;
    }
}

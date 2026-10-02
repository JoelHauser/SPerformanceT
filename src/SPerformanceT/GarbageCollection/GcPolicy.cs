using System;

namespace SPerformanceT.GarbageCollection
{
    public enum SafetyReason
    {
        None,
        HeapLimit,
        HeapGrowth,
        LowCommit,
    }

    /// <summary>
    /// The memory safety decisions, with no game or Unity dependency, so the tests link this file.
    /// Keep it that way.
    ///
    /// What these guard is the managed heap only. On Streets with a large mod list EFT commits around
    /// 30 GB, and ~27 GB of that is native memory (assets) that garbage collection cannot free. So low
    /// free physical RAM is not a reason to collect: a 2026-10-02 run did exactly that, freed
    /// 0.23 GB of a 3.1 GB heap, and fell back to automatic GC for nothing. What actually ends a
    /// process is the system commit limit, so that is the emergency signal.
    /// </summary>
    public static class GcPolicy
    {
        public const double BytesPerGb = 1024.0 * 1024.0 * 1024.0;

        /// <summary>
        /// After a heap-limit cleanup, GC goes back off only if the heap ends below this share of
        /// the limit. Otherwise most of the heap is live, and it would trip again a moment later.
        /// </summary>
        public const double RecoveredShareOfLimit = 0.75;

        /// <summary>Minimum seconds between two safety cleanups, so a stubborn heap can't loop.</summary>
        public const float SafetyCooldownSeconds = 30f;

        /// <summary>
        /// Should a safety cleanup start? Low commit first (the real danger), then the heap limit,
        /// then growth since the last cleanup. <paramref name="freeCommitBytes"/> below 0 means unknown.
        /// </summary>
        public static SafetyReason Check(long heapBytes, long heapBaselineBytes, long freeCommitBytes,
                                         double heapLimitGb, double growthGb, double minFreeCommitGb)
        {
            if (freeCommitBytes >= 0 && freeCommitBytes < minFreeCommitGb * BytesPerGb)
                return SafetyReason.LowCommit;
            if (heapBytes >= heapLimitGb * BytesPerGb)
                return SafetyReason.HeapLimit;
            if (heapBytes - heapBaselineBytes >= growthGb * BytesPerGb)
                return SafetyReason.HeapGrowth;
            return SafetyReason.None;
        }

        /// <summary>
        /// After a cleanup: may GC go back off, or should automatic GC stay on for the rest of the raid?
        /// </summary>
        public static bool SafeToTurnOffAgain(SafetyReason reason, long heapAfterBytes, double heapLimitGb)
        {
            switch (reason)
            {
                case SafetyReason.LowCommit:
                    return false; // the machine is nearly out of memory: every byte the heap would grow matters
                case SafetyReason.HeapLimit:
                    return heapAfterBytes < heapLimitGb * BytesPerGb * RecoveredShareOfLimit;
                default:
                    return true; // inventory and growth cleanups only bound the heap; the baseline moves on
            }
        }

        public static ulong SliceNanoseconds(float milliseconds)
        {
            if (float.IsNaN(milliseconds) || milliseconds <= 0f)
                milliseconds = 0.5f;
            milliseconds = Math.Min(milliseconds, 50f);
            return (ulong)Math.Round(milliseconds * 1_000_000.0);
        }

        public static string Gb(long bytes) =>
            (bytes / BytesPerGb).ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) + " GB";
    }
}

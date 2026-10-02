using System;

namespace SPerformanceT.GarbageCollection
{
    public enum SafetyReason
    {
        None,
        HeapLimit,
        LowFreeRam,
    }

    /// <summary>
    /// The memory safety decisions, with no game or Unity dependency, so the tests link this file.
    /// Keep it that way.
    /// </summary>
    public static class GcPolicy
    {
        public const double BytesPerGb = 1024.0 * 1024.0 * 1024.0;

        /// <summary>
        /// A cleanup counts as having worked only if the heap ends below this share of the limit.
        /// Otherwise most of the heap is live data, and turning GC off again would just trip the
        /// limit again a moment later.
        /// </summary>
        public const double RecoveredShareOfLimit = 0.75;

        /// <summary>Minimum seconds between two safety cleanups, so a stubborn heap can't loop.</summary>
        public const float SafetyCooldownSeconds = 30f;

        /// <summary>Should a safety cleanup start? The heap limit is checked first.</summary>
        public static SafetyReason Check(long heapBytes, long freeRamBytes, double heapLimitGb, double minFreeRamGb)
        {
            if (heapBytes >= heapLimitGb * BytesPerGb)
                return SafetyReason.HeapLimit;
            if (freeRamBytes >= 0 && freeRamBytes < minFreeRamGb * BytesPerGb)
                return SafetyReason.LowFreeRam;
            return SafetyReason.None;
        }

        /// <summary>
        /// After a safety cleanup: is it safe to turn GC off again, or should automatic GC stay on
        /// for the rest of the raid?
        /// </summary>
        public static bool SafeToTurnOffAgain(SafetyReason reason, long heapBeforeBytes, long heapAfterBytes,
                                              double heapLimitGb)
        {
            if (heapAfterBytes >= heapLimitGb * BytesPerGb * RecoveredShareOfLimit)
                return false;

            // Freed managed memory is reused by the heap but rarely handed back to Windows, so free
            // RAM barely moves after a cleanup. What matters is that the cleanup found real garbage:
            // at least a quarter of the heap. If it did not, RAM is short and the heap is mostly
            // live, so keep collecting.
            if (reason == SafetyReason.LowFreeRam)
                return heapAfterBytes <= heapBeforeBytes * 0.75;

            return true;
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

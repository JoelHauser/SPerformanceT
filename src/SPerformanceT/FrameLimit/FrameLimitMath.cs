using System;

namespace SPerformanceT.FrameLimit
{
    /// <summary>Pure; linked into the tests. Keep it free of game, Unity and NVIDIA types.</summary>
    public static class FrameLimitMath
    {
        public const int MaxFps = 1000;

        /// <summary>Reflex's minimumIntervalUs for a frame cap. 0 means no cap.</summary>
        public static uint IntervalUs(int fps)
        {
            if (fps <= 0)
                return 0;
            fps = Math.Min(fps, MaxFps);
            return (uint)Math.Round(1_000_000.0 / fps);
        }
    }
}

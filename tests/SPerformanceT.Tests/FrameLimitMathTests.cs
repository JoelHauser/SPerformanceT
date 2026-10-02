using SPerformanceT.FrameLimit;

namespace SPerformanceT.Tests;

public class FrameLimitMathTests
{
    [Theory]
    [InlineData(0, 0u)]
    [InlineData(-5, 0u)]
    [InlineData(60, 16667u)]
    [InlineData(141, 7092u)]
    [InlineData(144, 6944u)]
    [InlineData(158, 6329u)]
    [InlineData(1000, 1000u)]
    [InlineData(5000, 1000u)] // clamped to MaxFps
    public void IntervalIsOneSecondOverTheCap(int fps, uint expected)
    {
        Assert.Equal(expected, FrameLimitMath.IntervalUs(fps));
    }
}

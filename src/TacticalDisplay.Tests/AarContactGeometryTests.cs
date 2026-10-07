using TacticalLink.Server.Aar;
using Xunit;

namespace TacticalDisplay.Tests;

public sealed class AarContactGeometryTests
{
    [Theory]
    [InlineData(0, -1, 0)]
    [InlineData(90, 0, -1)]
    [InlineData(180, 1, 0)]
    [InlineData(270, 0, 1)]
    [InlineData(45, -0.70710678, -0.70710678)]
    public void HeadingRotatesTankerRelativeFrame(double heading, double receiverNorthDirection, double receiverEastDirection)
    {
        var time = DateTimeOffset.UtcNow;
        var tanker = new AarPose(time, 60, 25, 10_000, heading, 100, 0, 0);
        var receiver = OffsetPose(time, tanker, 100 * receiverNorthDirection, 100 * receiverEastDirection, -15, heading, 100, 0, 0);

        Assert.True(AarContactGeometry.TryMeasure(tanker, receiver, new AarContactConfiguration(), time, out var measured));
        Assert.InRange(measured.BehindMeters, 98, 102);
        Assert.InRange(Math.Abs(measured.LateralMeters), 0, 1.5);
        Assert.InRange(measured.BelowMeters, 14, 16);
        Assert.True(AarContactGeometry.IsInsideCapture(measured, new AarContactConfiguration()));
    }

    [Fact]
    public void CaptureAndReleaseUseDifferentHysteresisLimits()
    {
        var parameters = new AarContactConfiguration();
        var nearEdge = new AarRelativePosition(170, 22, 40, 5);

        Assert.False(AarContactGeometry.IsInsideCapture(nearEdge, parameters));
        Assert.True(AarContactGeometry.IsInsideRelease(nearEdge, parameters));
    }

    [Fact]
    public void StaleOrPoorlyAlignedPoseIsRejected()
    {
        var now = DateTimeOffset.UtcNow;
        var tanker = new AarPose(now.AddSeconds(-2), 60, 25, 10_000, 0, 0, 0, 0);
        var receiver = new AarPose(now, 59.999, 25, 9_990, 0, 0, 0, 0);

        Assert.False(AarContactGeometry.TryMeasure(tanker, receiver, new AarContactConfiguration(), now, out _));
    }

    [Fact]
    public void RelativeSpeedIsThreeDimensional()
    {
        var config = new AarContactConfiguration();
        var relative = new AarRelativePosition(50, 0, 10, 4.1);

        Assert.False(AarContactGeometry.IsInsideCapture(relative, config));
        Assert.True(AarContactGeometry.IsInsideRelease(relative, config));
    }

    [Fact]
    public void PoseInterpolationUsesShortestHeadingArcAndInterpolatesVelocity()
    {
        var start = DateTimeOffset.UtcNow;
        var first = new AarPose(start, 60, 25, 10_000, 350, 100, 10, 0);
        var second = new AarPose(start.AddMilliseconds(100), 60.001, 25.001, 10_100, 10, 120, 30, 4);

        var interpolated = AarContactGeometry.InterpolatePose(first, second, start.AddMilliseconds(50));

        Assert.Equal(start.AddMilliseconds(50), interpolated.TimestampUtc);
        Assert.InRange(Math.Min(interpolated.HeadingDeg, 360 - interpolated.HeadingDeg), 0, 0.01);
        Assert.Equal(110, interpolated.VelocityNorthMps);
        Assert.Equal(20, interpolated.VelocityEastMps);
        Assert.Equal(10_050, interpolated.AltitudeMeters);
    }

    private static AarPose OffsetPose(DateTimeOffset time, AarPose tanker, double northMeters, double eastMeters, double upMeters,
        double heading, double velocityNorth, double velocityEast, double velocityDown = 0)
    {
        var latitude = tanker.LatitudeDeg + northMeters / 111_320.0;
        var longitude = tanker.LongitudeDeg + eastMeters / (111_320.0 * Math.Cos(tanker.LatitudeDeg * Math.PI / 180));
        return new AarPose(time, latitude, longitude, tanker.AltitudeMeters + upMeters, heading, velocityNorth, velocityEast, velocityDown);
    }
}

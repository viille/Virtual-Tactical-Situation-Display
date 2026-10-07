using TacticalDisplay.App.TacticalLink;
using TacticalDisplay.Core.Models;
using Xunit;

namespace TacticalDisplay.Tests;

public sealed class AarPoseConverterTests
{
    [Fact]
    public void ConvertsXpOwnshipUnitsAndThreeDimensionalVelocityToMeters()
    {
        var start = Pose(60, 25, 10000, 0, 0, 100, DateTimeOffset.UnixEpoch);
        var current = Pose(60.01, 25.02, 10100, 45, 90, 100, DateTimeOffset.UnixEpoch.AddSeconds(1));

        var result = AarPoseConverter.Convert(current, start);

        Assert.Equal(current.Timestamp, result.TimestampUtc);
        Assert.Equal(60.01, result.LatitudeDeg);
        Assert.Equal(25.02, result.LongitudeDeg);
        Assert.Equal(10100 * 0.3048, result.AltitudeMeters, 5);
        Assert.Equal(45, result.HeadingDeg);
        Assert.Equal(100 * 0.514444 * Math.Cos(Math.PI / 2), result.VelocityNorthMps, 5);
        Assert.Equal(100 * 0.514444, result.VelocityEastMps, 5);
        Assert.Equal(-100 * 0.3048, result.VelocityDownMps, 5);
    }

    [Fact]
    public void UsesHeadingWhenGroundTrackIsMissingAndRejectsStaleVerticalDifference()
    {
        var previous = Pose(0, 0, 1000, 90, 90, 100, DateTimeOffset.UnixEpoch);
        var current = new OwnshipState("OWN", 0, 0, 1100, 90, 100,
            DateTimeOffset.UnixEpoch.AddSeconds(4), null, null);

        var result = AarPoseConverter.Convert(current, previous);

        Assert.Equal(0, result.VelocityNorthMps, 5);
        Assert.Equal(100 * 0.514444, result.VelocityEastMps, 5);
        Assert.Equal(0, result.VelocityDownMps);
    }

    private static OwnshipState Pose(double latitude, double longitude, double altitudeFt,
        double headingDeg, double trackDeg, double speedKt, DateTimeOffset timestamp) =>
        new("OWN", latitude, longitude, altitudeFt, headingDeg, speedKt, timestamp, null, trackDeg);
}

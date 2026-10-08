using TacticalDisplay.Core.Models;

namespace TacticalDisplay.App.TacticalLink;

internal sealed record AarPoseUpdate(
    DateTimeOffset TimestampUtc,
    double LatitudeDeg,
    double LongitudeDeg,
    double AltitudeMeters,
    double HeadingDeg,
    double VelocityNorthMps,
    double VelocityEastMps,
    double VelocityDownMps);

internal static class AarPoseConverter
{
    private const double MetersPerFoot = 0.3048;
    private const double MetersPerSecondPerKnot = 0.514444;

    public static AarPoseUpdate Convert(OwnshipState current, OwnshipState? previous)
    {
        var track = current.GroundTrackDeg ?? current.HeadingDeg;
        var speedMps = (current.SpeedKt ?? throw new ArgumentException("Ownship ground speed is required for AAR pose.")) * MetersPerSecondPerKnot;
        var radians = track * (Math.PI / 180.0);
        var velocityDownMps = 0d;
        if (previous is not null)
        {
            var elapsed = (current.Timestamp - previous.Timestamp).TotalSeconds;
            if (elapsed is > 0.05 and <= 3)
            {
                var vertical = (current.AltitudeFt - previous.AltitudeFt) * MetersPerFoot / elapsed;
                if (double.IsFinite(vertical) && Math.Abs(vertical) <= 100) velocityDownMps = -vertical;
            }
        }

        return new AarPoseUpdate(current.Timestamp, current.LatitudeDeg, current.LongitudeDeg,
            current.AltitudeFt * MetersPerFoot, current.HeadingDeg,
            speedMps * Math.Cos(radians), speedMps * Math.Sin(radians), velocityDownMps);
    }
}

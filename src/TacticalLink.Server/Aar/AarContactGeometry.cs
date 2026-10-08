namespace TacticalLink.Server.Aar;

public sealed record AarPose(
    DateTimeOffset TimestampUtc,
    double LatitudeDeg,
    double LongitudeDeg,
    double AltitudeMeters,
    double HeadingDeg,
    double VelocityNorthMps,
    double VelocityEastMps,
    double VelocityDownMps);

public sealed record AarContactConfiguration(
    double MinimumBehindMeters = 12,
    double MaximumBehindMeters = 160,
    double MaximumCaptureLateralMeters = 18,
    double MinimumBelowMeters = 2,
    double MaximumBelowMeters = 32,
    double MaximumCaptureRelativeSpeedMps = 4,
    double ReleaseMaximumBehindMeters = 210,
    double ReleaseMaximumLateralMeters = 28,
    double ReleaseMaximumBelowMeters = 48,
    double ReleaseMaximumRelativeSpeedMps = 7,
    TimeSpan? CaptureDebounce = null,
    TimeSpan? ReleaseDebounce = null,
    TimeSpan? MaximumPoseAge = null,
    TimeSpan? MaximumAlignmentGap = null)
{
    public TimeSpan EffectiveCaptureDebounce => CaptureDebounce ?? TimeSpan.FromSeconds(1);
    public TimeSpan EffectiveReleaseDebounce => ReleaseDebounce ?? TimeSpan.FromMilliseconds(250);
    public TimeSpan EffectiveMaximumPoseAge => MaximumPoseAge ?? TimeSpan.FromMilliseconds(750);
    public TimeSpan EffectiveMaximumAlignmentGap => MaximumAlignmentGap ?? TimeSpan.FromMilliseconds(200);
}

public readonly record struct AarRelativePosition(double BehindMeters, double LateralMeters, double BelowMeters, double RelativeSpeedMps);

public static class AarContactGeometry
{
    private const double Wgs84SemiMajorMeters = 6_378_137.0;
    private const double Wgs84EccentricitySquared = 6.69437999014e-3;

    public static bool TryMeasure(AarPose tanker, AarPose receiver, AarContactConfiguration configuration, DateTimeOffset evaluationTime, out AarRelativePosition relative)
    {
        relative = default;
        if (!IsValid(tanker) || !IsValid(receiver) ||
            evaluationTime - tanker.TimestampUtc > configuration.EffectiveMaximumPoseAge ||
            evaluationTime - receiver.TimestampUtc > configuration.EffectiveMaximumPoseAge ||
            Math.Abs((tanker.TimestampUtc - receiver.TimestampUtc).TotalMilliseconds) > configuration.EffectiveMaximumAlignmentGap.TotalMilliseconds)
            return false;

        var t = ToEcef(tanker);
        var r = ToEcef(receiver);
        var dx = r.X - t.X;
        var dy = r.Y - t.Y;
        var dz = r.Z - t.Z;
        var lat = DegreesToRadians(tanker.LatitudeDeg);
        var lon = DegreesToRadians(tanker.LongitudeDeg);
        var east = -Math.Sin(lon) * dx + Math.Cos(lon) * dy;
        var north = -Math.Sin(lat) * Math.Cos(lon) * dx - Math.Sin(lat) * Math.Sin(lon) * dy + Math.Cos(lat) * dz;
        var up = Math.Cos(lat) * Math.Cos(lon) * dx + Math.Cos(lat) * Math.Sin(lon) * dy + Math.Sin(lat) * dz;
        var heading = DegreesToRadians(tanker.HeadingDeg);
        var forward = north * Math.Cos(heading) + east * Math.Sin(heading);
        var right = east * Math.Cos(heading) - north * Math.Sin(heading);
        var relativeNorth = receiver.VelocityNorthMps - tanker.VelocityNorthMps;
        var relativeEast = receiver.VelocityEastMps - tanker.VelocityEastMps;
        var relativeDown = receiver.VelocityDownMps - tanker.VelocityDownMps;
        relative = new AarRelativePosition(-forward, right, -up,
            Math.Sqrt(relativeNorth * relativeNorth + relativeEast * relativeEast + relativeDown * relativeDown));
        return true;
    }

    public static bool IsInsideCapture(AarRelativePosition relative, AarContactConfiguration configuration) =>
        relative.BehindMeters >= configuration.MinimumBehindMeters && relative.BehindMeters <= configuration.MaximumBehindMeters &&
        Math.Abs(relative.LateralMeters) <= configuration.MaximumCaptureLateralMeters &&
        relative.BelowMeters >= configuration.MinimumBelowMeters && relative.BelowMeters <= configuration.MaximumBelowMeters &&
        relative.RelativeSpeedMps <= configuration.MaximumCaptureRelativeSpeedMps;

    public static bool IsInsideRelease(AarRelativePosition relative, AarContactConfiguration configuration) =>
        relative.BehindMeters >= configuration.MinimumBehindMeters * 0.5 && relative.BehindMeters <= configuration.ReleaseMaximumBehindMeters &&
        Math.Abs(relative.LateralMeters) <= configuration.ReleaseMaximumLateralMeters &&
        relative.BelowMeters >= 0 && relative.BelowMeters <= configuration.ReleaseMaximumBelowMeters &&
        relative.RelativeSpeedMps <= configuration.ReleaseMaximumRelativeSpeedMps;

    public static AarPose InterpolatePose(AarPose first, AarPose second, DateTimeOffset timestamp)
    {
        if (second.TimestampUtc <= first.TimestampUtc || timestamp < first.TimestampUtc || timestamp > second.TimestampUtc)
            throw new ArgumentOutOfRangeException(nameof(timestamp));
        var amount = (timestamp - first.TimestampUtc).TotalSeconds / (second.TimestampUtc - first.TimestampUtc).TotalSeconds;
        static double Lerp(double a, double b, double t) => a + ((b - a) * t);
        var headingDelta = ((second.HeadingDeg - first.HeadingDeg + 540) % 360) - 180;
        return new AarPose(timestamp,
            Lerp(first.LatitudeDeg, second.LatitudeDeg, amount),
            Lerp(first.LongitudeDeg, second.LongitudeDeg, amount),
            Lerp(first.AltitudeMeters, second.AltitudeMeters, amount),
            (first.HeadingDeg + headingDelta * amount + 360) % 360,
            Lerp(first.VelocityNorthMps, second.VelocityNorthMps, amount),
            Lerp(first.VelocityEastMps, second.VelocityEastMps, amount),
            Lerp(first.VelocityDownMps, second.VelocityDownMps, amount));
    }

    private static bool IsValid(AarPose pose) =>
        double.IsFinite(pose.LatitudeDeg) && pose.LatitudeDeg is >= -90 and <= 90 &&
        double.IsFinite(pose.LongitudeDeg) && pose.LongitudeDeg is >= -180 and <= 180 &&
        double.IsFinite(pose.AltitudeMeters) && pose.AltitudeMeters is >= -1000 and <= 100000 &&
        double.IsFinite(pose.HeadingDeg) && pose.HeadingDeg is >= 0 and < 360 &&
        double.IsFinite(pose.VelocityNorthMps) && double.IsFinite(pose.VelocityEastMps) && double.IsFinite(pose.VelocityDownMps);

    private static (double X, double Y, double Z) ToEcef(AarPose pose)
    {
        var lat = DegreesToRadians(pose.LatitudeDeg);
        var lon = DegreesToRadians(pose.LongitudeDeg);
        var sinLat = Math.Sin(lat);
        var cosLat = Math.Cos(lat);
        var radius = Wgs84SemiMajorMeters / Math.Sqrt(1 - Wgs84EccentricitySquared * sinLat * sinLat);
        return (
            (radius + pose.AltitudeMeters) * cosLat * Math.Cos(lon),
            (radius + pose.AltitudeMeters) * cosLat * Math.Sin(lon),
            (radius * (1 - Wgs84EccentricitySquared) + pose.AltitudeMeters) * sinLat);
    }

    private static double DegreesToRadians(double degrees) => degrees * (Math.PI / 180.0);
}

namespace TacticalDisplay.Core.Formatting;

public static class AviationFormat
{
    public static string RelativeAltitudeHundreds(double relAltFt)
    {
        var sign = relAltFt >= 0 ? "+" : "-";
        var hundreds = (int)System.Math.Round(System.Math.Abs(relAltFt) / 100.0, MidpointRounding.AwayFromZero);
        return $"{sign}{hundreds:00}";
    }

    public static string TargetAspect(double targetHeadingDeg, double bearingFromOwnshipToTargetDeg)
    {
        var bearingFromOtherAircraftToOwnship = NormalizeDegrees(bearingFromOwnshipToTargetDeg + 180.0);
        var aspectDelta = System.Math.Abs(NormalizeSignedBearing(bearingFromOtherAircraftToOwnship - targetHeadingDeg));
        if (aspectDelta <= 30.0)
        {
            return "HOT";
        }

        if (aspectDelta <= 70.0)
        {
            return "FLANK";
        }

        if (aspectDelta <= 120.0)
        {
            return "BEAM";
        }

        if (aspectDelta <= 160.0)
        {
            return "DRAG";
        }

        return "COLD";
    }

    private static double NormalizeDegrees(double angleDeg)
    {
        var normalized = angleDeg % 360.0;
        return normalized < 0 ? normalized + 360.0 : normalized;
    }

    private static double NormalizeSignedBearing(double bearingDeg)
    {
        var normalized = bearingDeg % 360.0;
        if (normalized > 180.0)
        {
            normalized -= 360.0;
        }
        else if (normalized < -180.0)
        {
            normalized += 360.0;
        }

        return normalized;
    }
}

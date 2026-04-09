namespace TacticalDisplay.Core.Math;

public static class GeoMath
{
    private const double EarthRadiusNm = 3440.065;

    public static double NormalizeDegrees(double angleDeg)
    {
        var normalized = angleDeg % 360.0;
        return normalized < 0 ? normalized + 360.0 : normalized;
    }

    public static double SignedRelativeBearingDeg(double ownHeadingDeg, double trueBearingDeg)
    {
        var delta = NormalizeDegrees(trueBearingDeg - ownHeadingDeg);
        return delta > 180.0 ? delta - 360.0 : delta;
    }

    public static double DistanceNm(double lat1Deg, double lon1Deg, double lat2Deg, double lon2Deg)
    {
        var lat1 = DegreesToRadians(lat1Deg);
        var lon1 = DegreesToRadians(lon1Deg);
        var lat2 = DegreesToRadians(lat2Deg);
        var lon2 = DegreesToRadians(lon2Deg);

        var dLat = lat2 - lat1;
        var dLon = lon2 - lon1;
        var a = System.Math.Pow(System.Math.Sin(dLat / 2), 2) +
                System.Math.Cos(lat1) * System.Math.Cos(lat2) * System.Math.Pow(System.Math.Sin(dLon / 2), 2);
        var c = 2 * System.Math.Atan2(System.Math.Sqrt(a), System.Math.Sqrt(1 - a));
        return EarthRadiusNm * c;
    }

    public static double InitialBearingDeg(double lat1Deg, double lon1Deg, double lat2Deg, double lon2Deg)
    {
        var lat1 = DegreesToRadians(lat1Deg);
        var lat2 = DegreesToRadians(lat2Deg);
        var dLon = DegreesToRadians(lon2Deg - lon1Deg);

        var y = System.Math.Sin(dLon) * System.Math.Cos(lat2);
        var x = System.Math.Cos(lat1) * System.Math.Sin(lat2) -
                System.Math.Sin(lat1) * System.Math.Cos(lat2) * System.Math.Cos(dLon);
        var bearing = RadiansToDegrees(System.Math.Atan2(y, x));
        return NormalizeDegrees(bearing);
    }

    public static (double latitudeDeg, double longitudeDeg) DestinationPoint(double latDeg, double lonDeg, double bearingDeg, double distanceNm)
    {
        var angularDistance = distanceNm / EarthRadiusNm;
        var bearingRad = DegreesToRadians(NormalizeDegrees(bearingDeg));
        var latRad = DegreesToRadians(latDeg);
        var lonRad = DegreesToRadians(lonDeg);

        var sinLat = System.Math.Sin(latRad);
        var cosLat = System.Math.Cos(latRad);
        var sinAd = System.Math.Sin(angularDistance);
        var cosAd = System.Math.Cos(angularDistance);

        var destLat = System.Math.Asin((sinLat * cosAd) + (cosLat * sinAd * System.Math.Cos(bearingRad)));
        var destLon = lonRad + System.Math.Atan2(
            System.Math.Sin(bearingRad) * sinAd * cosLat,
            cosAd - (sinLat * System.Math.Sin(destLat)));

        return (RadiansToDegrees(destLat), NormalizeLongitude(RadiansToDegrees(destLon)));
    }

    public static double RadialClosureKt(
        double ownHeadingDeg,
        double ownSpeedKt,
        double targetHeadingDeg,
        double targetSpeedKt,
        double bearingFromOwnshipToTargetDeg)
    {
        var ownVelocity = VelocityVector(ownHeadingDeg, ownSpeedKt);
        var targetVelocity = VelocityVector(targetHeadingDeg, targetSpeedKt);
        var lineOfSight = VelocityVector(bearingFromOwnshipToTargetDeg, 1);
        var relativeVelocityNorth = ownVelocity.north - targetVelocity.north;
        var relativeVelocityEast = ownVelocity.east - targetVelocity.east;
        return (relativeVelocityNorth * lineOfSight.north) + (relativeVelocityEast * lineOfSight.east);
    }

    private static (double north, double east) VelocityVector(double headingDeg, double speedKt)
    {
        var radians = DegreesToRadians(NormalizeDegrees(headingDeg));
        return (speedKt * System.Math.Cos(radians), speedKt * System.Math.Sin(radians));
    }

    private static double NormalizeLongitude(double longitudeDeg)
    {
        var normalized = (longitudeDeg + 540.0) % 360.0;
        return normalized - 180.0;
    }

    private static double DegreesToRadians(double degrees) => degrees * System.Math.PI / 180.0;
    private static double RadiansToDegrees(double radians) => radians * 180.0 / System.Math.PI;
}

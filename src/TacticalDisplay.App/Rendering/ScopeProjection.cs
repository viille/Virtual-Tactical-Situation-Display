using TacticalDisplay.Core.Math;

namespace TacticalDisplay.App.Rendering;

public static class ScopeProjection
{
    private const double MetersPerNauticalMile = 1852.0;
    private const double WebMercatorEarthRadiusMeters = 6378137.0;

    public static (double x, double y) ProjectToScope(
        double centerX,
        double centerY,
        double radiusPixels,
        double rangeNm,
        double bearingDegTrue,
        double selectedRangeNm,
        double ownHeadingDeg,
        bool headingUp,
        bool clampToRange = true)
    {
        var usedBearing = headingUp
            ? GeoMath.NormalizeDegrees(bearingDegTrue - ownHeadingDeg)
            : GeoMath.NormalizeDegrees(bearingDegTrue);

        var ratio = clampToRange ? System.Math.Clamp(rangeNm / selectedRangeNm, 0, 1) : rangeNm / selectedRangeNm;
        var pixelRadius = ratio * radiusPixels;
        var rad = usedBearing * System.Math.PI / 180.0;
        var x = centerX + pixelRadius * System.Math.Sin(rad);
        var y = centerY - pixelRadius * System.Math.Cos(rad);
        return (x, y);
    }

    public static (double x, double y) ProjectGeographicToScope(
        double centerX,
        double centerY,
        double radiusPixels,
        double ownLatitudeDeg,
        double ownLongitudeDeg,
        double targetLatitudeDeg,
        double targetLongitudeDeg,
        double selectedRangeNm,
        double ownHeadingDeg,
        bool headingUp,
        bool clampToRange = true)
    {
        var ownMercator = ToWebMercator(ownLatitudeDeg, ownLongitudeDeg);
        var targetMercator = ToWebMercator(targetLatitudeDeg, targetLongitudeDeg);
        var metersPerScopeRadius = System.Math.Max(selectedRangeNm, 1) * MetersPerNauticalMile;
        var latitudeScale = System.Math.Max(System.Math.Cos(ownLatitudeDeg * System.Math.PI / 180.0), 0.05);
        var pixelScale = radiusPixels * latitudeScale / metersPerScopeRadius;

        var eastPixels = (targetMercator.x - ownMercator.x) * pixelScale;
        var northPixels = (targetMercator.y - ownMercator.y) * pixelScale;
        if (headingUp)
        {
            var headingRad = GeoMath.NormalizeDegrees(ownHeadingDeg) * System.Math.PI / 180.0;
            var sin = System.Math.Sin(headingRad);
            var cos = System.Math.Cos(headingRad);
            var rotatedEast = (eastPixels * cos) - (northPixels * sin);
            var rotatedNorth = (eastPixels * sin) + (northPixels * cos);
            eastPixels = rotatedEast;
            northPixels = rotatedNorth;
        }

        if (clampToRange)
        {
            var length = System.Math.Sqrt((eastPixels * eastPixels) + (northPixels * northPixels));
            if (length > radiusPixels && length > 0)
            {
                var clampScale = radiusPixels / length;
                eastPixels *= clampScale;
                northPixels *= clampScale;
            }
        }

        return (centerX + eastPixels, centerY - northPixels);
    }

    private static (double x, double y) ToWebMercator(double latitudeDeg, double longitudeDeg)
    {
        var clampedLatitude = System.Math.Clamp(latitudeDeg, -85.05112878, 85.05112878);
        var latitudeRad = clampedLatitude * System.Math.PI / 180.0;
        var longitudeRad = longitudeDeg * System.Math.PI / 180.0;
        return (
            WebMercatorEarthRadiusMeters * longitudeRad,
            WebMercatorEarthRadiusMeters * System.Math.Log(System.Math.Tan((System.Math.PI / 4.0) + (latitudeRad / 2.0))));
    }
}

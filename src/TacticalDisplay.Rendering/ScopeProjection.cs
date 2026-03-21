using TacticalDisplay.Core.Math;

namespace TacticalDisplay.Rendering;

public static class ScopeProjection
{
    public static (double x, double y) ProjectToScope(
        double centerX,
        double centerY,
        double radiusPixels,
        double rangeNm,
        double bearingDegTrue,
        double selectedRangeNm,
        double ownHeadingDeg,
        bool headingUp)
    {
        var usedBearing = headingUp
            ? GeoMath.NormalizeDegrees(bearingDegTrue - ownHeadingDeg)
            : GeoMath.NormalizeDegrees(bearingDegTrue);

        var ratio = System.Math.Clamp(rangeNm / selectedRangeNm, 0, 1);
        var pixelRadius = ratio * radiusPixels;
        var rad = usedBearing * System.Math.PI / 180.0;
        var x = centerX + pixelRadius * System.Math.Sin(rad);
        var y = centerY - pixelRadius * System.Math.Cos(rad);
        return (x, y);
    }
}

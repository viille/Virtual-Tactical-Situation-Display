using TacticalDisplay.Core.Math;
using TacticalDisplay.Core.Models;

namespace TacticalDisplay.Core.Services;

public static class TacticalInterceptCalculator
{
    public static InterceptSolution Calculate(OwnshipState ownship, ComputedTarget target)
    {
        var ownSpeedKt = ownship.SpeedKt ?? 0;
        var targetSpeedKt = target.SpeedKt ?? 0;
        var targetHeadingDeg = target.HeadingDeg ?? 0;
        if (ownSpeedKt <= 1 || target.RangeNm <= 0)
        {
            return InterceptSolution.None;
        }

        var targetBearingRad = target.BearingDegTrue * System.Math.PI / 180.0;
        var targetX = target.RangeNm * System.Math.Sin(targetBearingRad);
        var targetY = target.RangeNm * System.Math.Cos(targetBearingRad);
        var targetHeadingRad = targetHeadingDeg * System.Math.PI / 180.0;
        var targetVelocityX = targetSpeedKt * System.Math.Sin(targetHeadingRad);
        var targetVelocityY = targetSpeedKt * System.Math.Cos(targetHeadingRad);
        var a = (targetVelocityX * targetVelocityX) + (targetVelocityY * targetVelocityY) - (ownSpeedKt * ownSpeedKt);
        var b = 2 * ((targetX * targetVelocityX) + (targetY * targetVelocityY));
        var c = (targetX * targetX) + (targetY * targetY);
        var timeHours = SolvePositiveInterceptTimeHours(a, b, c);
        if (!timeHours.HasValue)
        {
            return InterceptSolution.None;
        }

        var interceptX = targetX + (targetVelocityX * timeHours.Value);
        var interceptY = targetY + (targetVelocityY * timeHours.Value);
        var interceptRange = System.Math.Sqrt((interceptX * interceptX) + (interceptY * interceptY));
        if (interceptRange <= 0.001)
        {
            return InterceptSolution.None;
        }

        var headingDeg = GeoMath.NormalizeDegrees(System.Math.Atan2(interceptX, interceptY) * 180.0 / System.Math.PI);
        var destination = GeoMath.DestinationPoint(
            ownship.LatitudeDeg,
            ownship.LongitudeDeg,
            headingDeg,
            interceptRange);

        return new InterceptSolution(
            true,
            headingDeg,
            timeHours.Value * 3600.0,
            destination.latitudeDeg,
            destination.longitudeDeg);
    }

    private static double? SolvePositiveInterceptTimeHours(double a, double b, double c)
    {
        const double epsilon = 1e-9;
        if (System.Math.Abs(a) < epsilon)
        {
            if (System.Math.Abs(b) < epsilon)
            {
                return null;
            }

            var linear = -c / b;
            return linear > 0 ? linear : null;
        }

        var discriminant = (b * b) - (4 * a * c);
        if (discriminant < 0)
        {
            return null;
        }

        var root = System.Math.Sqrt(discriminant);
        var t1 = (-b - root) / (2 * a);
        var t2 = (-b + root) / (2 * a);
        return new[] { t1, t2 }
            .Where(t => t > 0)
            .OrderBy(t => t)
            .Cast<double?>()
            .FirstOrDefault();
    }
}

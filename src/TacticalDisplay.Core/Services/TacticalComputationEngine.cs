using TacticalDisplay.Core.Math;
using TacticalDisplay.Core.Models;

namespace TacticalDisplay.Core.Services;

public sealed class TacticalComputationEngine
{
    public ComputedTarget? Compute(
        OwnshipState? previousOwnship,
        OwnshipState ownship,
        TrafficRepository.TrackedContact tracked,
        TacticalDisplaySettings settings)
    {
        var contact = tracked.Current;
        var rangeNm = GeoMath.DistanceNm(ownship.LatitudeDeg, ownship.LongitudeDeg, contact.LatitudeDeg, contact.LongitudeDeg);
        if (!PassRangeFilter(rangeNm, settings.RangeFilter))
        {
            return null;
        }

        var relAltFt = contact.AltitudeFt - ownship.AltitudeFt;
        if (!PassAltitudeFilter(relAltFt, settings.AltitudeFilter))
        {
            return null;
        }

        var trueBearing = GeoMath.InitialBearingDeg(ownship.LatitudeDeg, ownship.LongitudeDeg, contact.LatitudeDeg, contact.LongitudeDeg);
        var relativeBearing = GeoMath.SignedRelativeBearingDeg(ownship.HeadingDeg, trueBearing);
        var category = tracked.IsStale ? TargetCategory.Stale : tracked.Category;
        if (!PassCategoryFilter(category, settings.CategoryFilter))
        {
            return null;
        }

        var label = contact.Id;
        var history = settings.TrailsEnabled ? tracked.History : [];
        return new ComputedTarget(
            contact.Id,
            label,
            category,
            tracked.IsStale,
            rangeNm,
            trueBearing,
            relativeBearing,
            relAltFt,
            contact.HeadingDeg,
            contact.SpeedKt,
            tracked.EstimateClosureKt(previousOwnship, ownship),
            history,
            contact.Timestamp);
    }

    private static bool PassRangeFilter(double rangeNm, RangeFilterMode mode) =>
        mode switch
        {
            RangeFilterMode.Within50Nm => rangeNm <= 50,
            RangeFilterMode.Within20Nm => rangeNm <= 20,
            _ => true
        };

    private static bool PassAltitudeFilter(double relAltFt, AltitudeFilterMode mode) =>
        mode switch
        {
            AltitudeFilterMode.PlusMinus5000Ft => System.Math.Abs(relAltFt) <= 5000,
            AltitudeFilterMode.PlusMinus10000Ft => System.Math.Abs(relAltFt) <= 10000,
            _ => true
        };

    private static bool PassCategoryFilter(TargetCategory category, CategoryFilterMode mode) =>
        mode switch
        {
            CategoryFilterMode.FriendlyOnly => category is TargetCategory.Friend or TargetCategory.Package or TargetCategory.Support,
            CategoryFilterMode.PackageOnly => category == TargetCategory.Package,
            CategoryFilterMode.UnknownOnly => category is TargetCategory.Unknown or TargetCategory.Stale,
            _ => true
        };
}

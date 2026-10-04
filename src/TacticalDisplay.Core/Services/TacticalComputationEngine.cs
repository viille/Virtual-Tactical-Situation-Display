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
        var geometryContact = tracked.DisplayContactForCurrent ?? contact;
        var displayOwnship = tracked.DisplayOwnshipForCurrent;
        if (displayOwnship is null)
        {
            return null;
        }
        if (!PassTrackedAltitude(contact.AltitudeFt, settings.MinTrackedAltitudeFt, settings.MaxTrackedAltitudeFt))
        {
            return null;
        }

        var rangeNm = GeoMath.DistanceNm(displayOwnship.LatitudeDeg, displayOwnship.LongitudeDeg, geometryContact.LatitudeDeg, geometryContact.LongitudeDeg);
        if (rangeNm > settings.SelectedRangeNm)
        {
            return null;
        }

        var relAltFt = geometryContact.AltitudeFt - displayOwnship.AltitudeFt;
        var trueBearing = GeoMath.InitialBearingDeg(displayOwnship.LatitudeDeg, displayOwnship.LongitudeDeg, geometryContact.LatitudeDeg, geometryContact.LongitudeDeg);
        var relativeBearing = GeoMath.SignedRelativeBearingDeg(displayOwnship.HeadingDeg, trueBearing);
        var category = tracked.IsStale ? TargetCategory.Stale : tracked.Category;
        if (!PassCategoryFilter(category, settings.CategoryFilter))
        {
            return null;
        }

        var label = string.IsNullOrWhiteSpace(contact.Callsign)
            ? contact.Id
            : contact.Callsign.Trim().ToUpperInvariant();
        var history = settings.TrailsEnabled ? tracked.History : [];
        var closureKt = tracked.EstimateClosureKt(previousOwnship, ownship, trueBearing);
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
            closureKt,
            history,
            contact.Timestamp);
    }

    private static bool PassTrackedAltitude(double altitudeFt, double minTrackedAltitudeFt, double maxTrackedAltitudeFt) =>
        altitudeFt >= minTrackedAltitudeFt &&
        altitudeFt <= maxTrackedAltitudeFt;

    private static bool PassCategoryFilter(TargetCategory category, CategoryFilterMode mode) =>
        mode switch
        {
            CategoryFilterMode.FriendlyOnly => category is TargetCategory.Friend or TargetCategory.Package or TargetCategory.Support,
            CategoryFilterMode.PackageOnly => category == TargetCategory.Package,
            CategoryFilterMode.UnknownOnly => category is TargetCategory.Unknown or TargetCategory.Stale,
            _ => true
        };
}

using TacticalDisplay.Core.Math;
using TacticalDisplay.Core.Models;

namespace TacticalDisplay.Core.Services;

public sealed class TrafficRepository
{
    private static readonly TimeSpan StationaryDuplicateGracePeriod = TimeSpan.FromSeconds(5);
    private const double StationarySpeedThresholdKt = 3;
    private const double MeaningfulMovementThresholdNm = 0.02;
    private static readonly TimeSpan RelativeRangeHistoryWindow = TimeSpan.FromSeconds(6);
    private static readonly TimeSpan MinimumPositionClosureWindow = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan MaximumPositionClosureGap = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan ContactIdentityGap = TimeSpan.FromSeconds(10);
    private const double MaximumPlausibleTargetGroundSpeedKt = 2000;
    private const double MaximumPlausibleClosureKt = 3000;
    private readonly Dictionary<string, TrackedContact> _contacts = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, SuppressedContact> _suppressedContacts = new(StringComparer.OrdinalIgnoreCase);
    private OwnshipState? _previousOwnship;
    private OwnshipState? _ownship;

    public OwnshipState? Ownship => _ownship;

    public void ApplySnapshot(
        TrafficSnapshot snapshot,
        ClassificationConfig classification,
        TacticalDisplaySettings settings)
    {
        _previousOwnship = _ownship;
        _ownship = snapshot.Ownship;
        foreach (var contact in snapshot.Contacts)
        {
            if (ShouldSuppressStationaryContact(contact))
            {
                _contacts.Remove(contact.Id);
                continue;
            }

            if (!PassTrackedAltitude(contact.AltitudeFt, settings.MinTrackedAltitudeFt, settings.MaxTrackedAltitudeFt))
            {
                _contacts.Remove(contact.Id);
                continue;
            }

            if (!_contacts.TryGetValue(contact.Id, out var tracked))
            {
                tracked = new TrackedContact(contact, snapshot.Ownship, Classify(contact, classification));
                _contacts[contact.Id] = tracked;
            }

            var enrichedContact = tracked.ResolveCallsign(contact);
            tracked.Update(enrichedContact, snapshot.Ownship, settings.TrailLengthSamples, Classify(enrichedContact, classification));
        }

        var staleCutoff = snapshot.Timestamp - TimeSpan.FromSeconds(settings.StaleSeconds);
        var removeCutoff = snapshot.Timestamp - TimeSpan.FromSeconds(settings.RemoveAfterSeconds);
        var removeIds = new List<string>();

        foreach (var pair in _contacts)
        {
            pair.Value.IsStale = pair.Value.LastUpdate < staleCutoff;
            if (pair.Value.LastUpdate < removeCutoff)
            {
                removeIds.Add(pair.Key);
            }
        }

        foreach (var removeId in removeIds)
        {
            _contacts.Remove(removeId);
        }

        var suppressedRemoveCutoff = snapshot.Timestamp -
            TimeSpan.FromSeconds(System.Math.Max(settings.RemoveAfterSeconds, 1));
        foreach (var removeId in _suppressedContacts
                     .Where(pair => pair.Value.LastSeen < suppressedRemoveCutoff)
                     .Select(pair => pair.Key)
                     .ToList())
        {
            _suppressedContacts.Remove(removeId);
        }
    }

    public TacticalPicture BuildPicture(TacticalDisplaySettings settings)
    {
        if (_ownship is null)
        {
            throw new InvalidOperationException("Ownship not available yet.");
        }

        var engine = new TacticalComputationEngine();
        var targets = new List<ComputedTarget>(_contacts.Count);

        foreach (var contact in _contacts.Values)
        {
            var computed = engine.Compute(_previousOwnship, _ownship, contact, settings);
            if (computed is null)
            {
                continue;
            }

            targets.Add(computed);
        }

        return new TacticalPicture(_ownship, targets, DateTimeOffset.UtcNow);
    }

    public int Count => _contacts.Count;

    public TrackedContact? GetTrackedContact(string id) =>
        _contacts.TryGetValue(id, out var tracked) ? tracked : null;

    public static string FormatClosureLabel(double? closureKt) =>
        closureKt.HasValue ? $"C{closureKt.Value:+0;-0;+0}" : "C---";

    private static bool PassTrackedAltitude(double altitudeFt, double minTrackedAltitudeFt, double maxTrackedAltitudeFt) =>
        altitudeFt >= minTrackedAltitudeFt &&
        altitudeFt <= maxTrackedAltitudeFt;

    private bool ShouldSuppressStationaryContact(TrafficContactState contact)
    {
        if (!string.IsNullOrWhiteSpace(contact.Callsign) || !IsStationary(contact))
        {
            _suppressedContacts.Remove(contact.Id);
            return false;
        }

        if (!_suppressedContacts.TryGetValue(contact.Id, out var previous))
        {
            RememberSuppressedContact(contact);
            return false;
        }

        if (HasMeaningfulMovement(previous.Contact, contact))
        {
            _suppressedContacts.Remove(contact.Id);
            return false;
        }

        RememberSuppressedContact(contact);

        return contact.Timestamp - previous.FirstSeen >= StationaryDuplicateGracePeriod;
    }

    private void RememberSuppressedContact(TrafficContactState contact)
    {
        if (_suppressedContacts.TryGetValue(contact.Id, out var previous))
        {
            _suppressedContacts[contact.Id] = previous with
            {
                Contact = contact,
                LastSeen = contact.Timestamp
            };
            return;
        }

        _suppressedContacts[contact.Id] = new SuppressedContact(contact, contact.Timestamp, contact.Timestamp);
    }

    private static bool IsStationary(TrafficContactState contact) =>
        !contact.SpeedKt.HasValue || contact.SpeedKt.Value <= StationarySpeedThresholdKt;

    private static bool HasMeaningfulMovement(TrafficContactState previous, TrafficContactState current) =>
        GeoMath.DistanceNm(previous.LatitudeDeg, previous.LongitudeDeg, current.LatitudeDeg, current.LongitudeDeg) >= MeaningfulMovementThresholdNm ||
        System.Math.Abs(previous.AltitudeFt - current.AltitudeFt) >= 100;

    private static TargetCategory Classify(TrafficContactState contact, ClassificationConfig classification)
    {
        if (contact.Callsign is null)
        {
            return TargetCategory.Unknown;
        }

        var normalized = contact.Callsign.Trim().ToUpperInvariant();
        if (classification.PackageCallsigns.Contains(normalized))
        {
            return TargetCategory.Package;
        }

        if (classification.SupportCallsigns.Contains(normalized))
        {
            return TargetCategory.Support;
        }

        if (classification.FriendCallsigns.Contains(normalized))
        {
            return TargetCategory.Friend;
        }

        return TargetCategory.Unknown;
    }

    public sealed class TrackedContact
    {
        private readonly List<RelativeRangePoint> _relativeRangeHistory = [];

        public TrackedContact(TrafficContactState current, OwnshipState ownship, TargetCategory category)
        {
            LastKnownCallsign = string.IsNullOrWhiteSpace(current.Callsign)
                ? null
                : current.Callsign.Trim().ToUpperInvariant();
            Current = string.IsNullOrWhiteSpace(current.Callsign) || LastKnownCallsign is null
                ? current
                : current with { Callsign = LastKnownCallsign };
            Category = category;
            LastUpdate = current.Timestamp;
            History = [new PositionHistoryPoint(current.LatitudeDeg, current.LongitudeDeg, current.AltitudeFt, current.Timestamp)];
            RelativeRangeNm = GeoMath.DistanceNm(ownship.LatitudeDeg, ownship.LongitudeDeg, current.LatitudeDeg, current.LongitudeDeg);
            _relativeRangeHistory.Add(new RelativeRangePoint(RelativeRangeNm.Value, current.Timestamp));
        }

        public TrafficContactState Current { get; private set; }
        public string? LastKnownCallsign { get; private set; }
        public TargetCategory Category { get; private set; }
        public bool IsStale { get; set; }
        public DateTimeOffset LastUpdate { get; private set; }
        public List<PositionHistoryPoint> History { get; }
        public double? LastKnownClosureKt { get; private set; }
        public double? VectorClosureKt { get; private set; }
        public double? PositionClosureKt { get; private set; }
        public string ClosureSource { get; private set; } = "unavailable";
        public double? RelativeRangeNm { get; private set; }

        public void Update(TrafficContactState update, OwnshipState ownship, int trailLength, TargetCategory category)
        {
            if (update.Timestamp <= LastUpdate)
            {
                return;
            }

            var gap = update.Timestamp - LastUpdate;
            if (gap <= TimeSpan.Zero)
            {
                return;
            }

            var rangeNm = GeoMath.DistanceNm(ownship.LatitudeDeg, ownship.LongitudeDeg, update.LatitudeDeg, update.LongitudeDeg);
            var identityReset = gap > ContactIdentityGap;
            var jumpReset = IsImplausibleTargetPositionJump(update, gap);
            if (identityReset || jumpReset)
            {
                _relativeRangeHistory.Clear();
                History.Clear();
                LastKnownClosureKt = null;
                PositionClosureKt = null;
                VectorClosureKt = null;
                ClosureSource = "unavailable";
            }

            Current = update;
            Category = category;
            LastUpdate = update.Timestamp;
            History.Add(new PositionHistoryPoint(update.LatitudeDeg, update.LongitudeDeg, update.AltitudeFt, update.Timestamp));
            if (History.Count > trailLength)
            {
                History.RemoveAt(0);
            }

            RelativeRangeNm = rangeNm;
            _relativeRangeHistory.Add(new RelativeRangePoint(rangeNm, update.Timestamp));
            _relativeRangeHistory.RemoveAll(point => update.Timestamp - point.Timestamp > RelativeRangeHistoryWindow);
        }

        public TrafficContactState ResolveCallsign(TrafficContactState update)
        {
            if (!string.IsNullOrWhiteSpace(update.Callsign))
            {
                LastKnownCallsign = update.Callsign.Trim().ToUpperInvariant();
                return update with { Callsign = LastKnownCallsign };
            }

            return string.IsNullOrWhiteSpace(LastKnownCallsign)
                ? update
                : update with { Callsign = LastKnownCallsign };
        }

        public double? EstimateClosureKt(OwnshipState? previousOwnship, OwnshipState ownship, double bearingFromOwnshipToTargetDeg)
        {
            PositionClosureKt = null;
            VectorClosureKt = null;
            PositionClosureKt = EstimatePositionClosure();
            if (ownship.SpeedKt.HasValue && Current.SpeedKt.HasValue && Current.HeadingDeg.HasValue)
            {
                VectorClosureKt = GeoMath.RadialClosureKt(
                    ownship.HeadingDeg,
                    ownship.SpeedKt.Value,
                    Current.HeadingDeg.Value,
                    Current.SpeedKt.Value,
                    bearingFromOwnshipToTargetDeg);
            }

            if (PositionClosureKt.HasValue)
            {
                ClosureSource = "position";
                LastKnownClosureKt = PositionClosureKt;
                return PositionClosureKt;
            }

            if (VectorClosureKt.HasValue)
            {
                ClosureSource = "vector";
                LastKnownClosureKt = VectorClosureKt;
                return VectorClosureKt;
            }

            ClosureSource = LastKnownClosureKt.HasValue ? "last-known" : "unavailable";
            return LastKnownClosureKt;
        }

        private bool IsImplausibleTargetPositionJump(TrafficContactState update, TimeSpan gap)
        {
            if (History.Count == 0 || gap <= TimeSpan.Zero)
            {
                return false;
            }

            var previous = History[^1];
            var targetDistanceNm = GeoMath.DistanceNm(previous.LatitudeDeg, previous.LongitudeDeg, update.LatitudeDeg, update.LongitudeDeg);
            var apparentTargetGroundSpeedKt = targetDistanceNm / gap.TotalHours;
            return apparentTargetGroundSpeedKt > MaximumPlausibleTargetGroundSpeedKt;
        }

        private double? EstimatePositionClosure()
        {
            if (_relativeRangeHistory.Count < 3)
            {
                return null;
            }

            var latest = _relativeRangeHistory[^1].Timestamp;
            var points = _relativeRangeHistory
                .Where(point => latest - point.Timestamp <= RelativeRangeHistoryWindow)
                .ToArray();
            if (points.Length < 3 || latest - points[0].Timestamp < MinimumPositionClosureWindow ||
                latest - points[^2].Timestamp > MaximumPositionClosureGap)
            {
                return null;
            }

            var origin = points[0].Timestamp;
            var xs = points.Select(point => (point.Timestamp - origin).TotalHours).ToArray();
            var meanX = xs.Average();
            var meanY = points.Average(point => point.RangeNm);
            var denominator = xs.Sum(x => (x - meanX) * (x - meanX));
            if (denominator <= 0)
            {
                return null;
            }

            var slope = points.Select((point, index) => (xs[index] - meanX) * (point.RangeNm - meanY)).Sum() / denominator;
            var closure = -slope;
            return double.IsFinite(closure) && System.Math.Abs(closure) <= MaximumPlausibleClosureKt
                ? closure
                : null;
        }

        private sealed record RelativeRangePoint(double RangeNm, DateTimeOffset Timestamp);
    }

    private sealed record SuppressedContact(
        TrafficContactState Contact,
        DateTimeOffset FirstSeen,
        DateTimeOffset LastSeen);
}

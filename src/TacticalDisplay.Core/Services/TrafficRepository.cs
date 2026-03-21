using TacticalDisplay.Core.Math;
using TacticalDisplay.Core.Models;

namespace TacticalDisplay.Core.Services;

public sealed class TrafficRepository
{
    private readonly Dictionary<string, TrackedContact> _contacts = new(StringComparer.OrdinalIgnoreCase);
    private OwnshipState? _ownship;

    public OwnshipState? Ownship => _ownship;

    public void ApplySnapshot(
        TrafficSnapshot snapshot,
        ClassificationConfig classification,
        TacticalDisplaySettings settings)
    {
        _ownship = snapshot.Ownship;
        foreach (var contact in snapshot.Contacts)
        {
            if (!_contacts.TryGetValue(contact.Id, out var tracked))
            {
                tracked = new TrackedContact(contact, Classify(contact, classification));
                _contacts[contact.Id] = tracked;
            }

            tracked.Update(contact, settings.TrailLengthSamples, Classify(contact, classification));
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
            var computed = engine.Compute(_ownship, contact, settings);
            if (computed is null)
            {
                continue;
            }

            targets.Add(computed);
        }

        return new TacticalPicture(_ownship, targets, DateTimeOffset.UtcNow);
    }

    public int Count => _contacts.Count;

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
        public TrackedContact(TrafficContactState current, TargetCategory category)
        {
            Current = current;
            Category = category;
            LastUpdate = current.Timestamp;
            History = [new PositionHistoryPoint(current.LatitudeDeg, current.LongitudeDeg, current.AltitudeFt, current.Timestamp)];
        }

        public TrafficContactState Current { get; private set; }
        public TargetCategory Category { get; private set; }
        public bool IsStale { get; set; }
        public DateTimeOffset LastUpdate { get; private set; }
        public List<PositionHistoryPoint> History { get; }

        public void Update(TrafficContactState update, int trailLength, TargetCategory category)
        {
            Current = update;
            Category = category;
            LastUpdate = update.Timestamp;
            History.Add(new PositionHistoryPoint(update.LatitudeDeg, update.LongitudeDeg, update.AltitudeFt, update.Timestamp));
            if (History.Count > trailLength)
            {
                History.RemoveAt(0);
            }
        }

        public double? EstimateClosureKt(OwnshipState ownship)
        {
            if (History.Count < 2)
            {
                return null;
            }

            var prev = History[^2];
            var curr = History[^1];
            var ownToPrev = GeoMath.DistanceNm(ownship.LatitudeDeg, ownship.LongitudeDeg, prev.LatitudeDeg, prev.LongitudeDeg);
            var ownToCurr = GeoMath.DistanceNm(ownship.LatitudeDeg, ownship.LongitudeDeg, curr.LatitudeDeg, curr.LongitudeDeg);
            var dtHours = (curr.Timestamp - prev.Timestamp).TotalHours;
            if (dtHours <= 0)
            {
                return null;
            }

            return (ownToPrev - ownToCurr) / dtHours;
        }
    }
}

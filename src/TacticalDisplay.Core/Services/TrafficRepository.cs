using TacticalDisplay.Core.Math;
using TacticalDisplay.Core.Models;

namespace TacticalDisplay.Core.Services;

public sealed class TrafficRepository
{
    private static readonly TimeSpan StationaryDuplicateGracePeriod = TimeSpan.FromSeconds(5);
    private const double StationarySpeedThresholdKt = 3;
    private const double MeaningfulMovementThresholdNm = 0.02;
    private static readonly TimeSpan RelativeRangeHistoryWindow = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan MinimumPositionClosureWindow = TimeSpan.FromSeconds(1.5);
    private static readonly TimeSpan MaximumPositionClosureGap = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan ContactIdentityGap = TimeSpan.FromSeconds(10);
    private const double MaximumPlausibleTargetGroundSpeedKt = 2000;
    private const double MaximumPlausibleClosureKt = 3000;
    private const double MinimumRangeSampleWeight = 0.5;
    private const double MaximumRangeSampleWeight = 2.0;
    private readonly Dictionary<string, TrackedContact> _contacts = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, SuppressedContact> _suppressedContacts = new(StringComparer.OrdinalIgnoreCase);
    private OwnshipState? _previousOwnship;
    private OwnshipState? _ownship;
    private readonly List<OwnshipState> _ownshipHistory = [];

    public OwnshipState? Ownship => _ownship;

    public void ApplySnapshot(
        TrafficSnapshot snapshot,
        ClassificationConfig classification,
        TacticalDisplaySettings settings)
    {
        var ownshipSessionChanged = _ownship is not null &&
            _ownship.SessionGeneration != snapshot.Ownship.SessionGeneration;
        _previousOwnship = ownshipSessionChanged ? null : _ownship;
        if (ownshipSessionChanged)
        {
            _ownshipHistory.Clear();
            _contacts.Clear();
            _suppressedContacts.Clear();
        }
        _ownship = snapshot.Ownship;
        _ownshipHistory.Add(snapshot.Ownship);
        _ownshipHistory.RemoveAll(sample => snapshot.Timestamp - sample.Timestamp > TimeSpan.FromSeconds(6));
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

            if (!_contacts.TryGetValue(contact.Id, out var tracked) || tracked.Generation != contact.Generation)
            {
                tracked = new TrackedContact(contact, snapshot.Ownship, Classify(contact, classification), _ownshipHistory);
                _contacts[contact.Id] = tracked;
            }

            tracked.PrepareGeneration(contact);
            var enrichedContact = tracked.ResolveCallsign(contact);
            tracked.Update(enrichedContact, snapshot.Ownship, settings.TrailLengthSamples, Classify(enrichedContact, classification), _ownshipHistory, snapshot.Timestamp);
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
        private OwnshipState? _alignedOwnship;
        private bool _hasAlignedCurrentPosition;
        private DateTimeOffset? _positionSourceTransitionStartedAt;

        public TrackedContact(TrafficContactState current, OwnshipState ownship, TargetCategory category, IReadOnlyList<OwnshipState>? ownshipHistory = null)
        {
            LastKnownCallsign = string.IsNullOrWhiteSpace(current.Callsign)
                ? null
                : current.Callsign.Trim().ToUpperInvariant();
            Current = string.IsNullOrWhiteSpace(current.Callsign) || LastKnownCallsign is null
                ? current
                : current with { Callsign = LastKnownCallsign };
            Category = category;
            LastUpdate = current.Timestamp;
            Generation = current.Generation;
            History = [new PositionHistoryPoint(current.LatitudeDeg, current.LongitudeDeg, current.AltitudeFt, current.Timestamp)];
            var alignedOwnship = AlignOwnship(ownshipHistory ?? [ownship], current.Timestamp);
            _alignedOwnship = alignedOwnship;
            _hasAlignedCurrentPosition = alignedOwnship is not null;
            OwnshipSampleAgeSeconds = System.Math.Max(0, (current.Timestamp - ownship.Timestamp).TotalSeconds);
            TargetSampleAgeSeconds = System.Math.Max(0, (ownship.Timestamp - current.Timestamp).TotalSeconds);
            TimestampSkewSeconds = (ownship.Timestamp - current.Timestamp).TotalSeconds;
            AlignmentMethod = GetAlignmentMethod(ownshipHistory ?? [ownship], current.Timestamp);
            if (alignedOwnship is not null)
            {
                RelativeRangeNm = GeoMath.DistanceNm(alignedOwnship.LatitudeDeg, alignedOwnship.LongitudeDeg, current.LatitudeDeg, current.LongitudeDeg);
                _relativeRangeHistory.Add(new RelativeRangePoint(RelativeRangeNm.Value, current.Timestamp));
            }
        }

        public TrafficContactState Current { get; private set; }
        public OwnshipState? AlignedOwnshipForCurrent => _alignedOwnship;
        public string? LastKnownCallsign { get; private set; }
        public TargetCategory Category { get; private set; }
        public bool IsStale { get; set; }
        public DateTimeOffset LastUpdate { get; private set; }
        public long Generation { get; }
        public List<PositionHistoryPoint> History { get; }
        public double? LastKnownClosureKt { get; private set; }
        public double? VectorClosureKt { get; private set; }
        public double? PositionClosureKt { get; private set; }
        public string ClosureSource { get; private set; } = "unavailable";
        public double? RelativeRangeNm { get; private set; }
        public double OwnshipSampleAgeSeconds { get; private set; }
        public double TargetSampleAgeSeconds { get; private set; }
        public double TimestampSkewSeconds { get; private set; }
        public string AlignmentMethod { get; private set; } = "unavailable";
        public int RegressionSampleCount { get; private set; }
        public double RegressionWindowSeconds { get; private set; }
        public double RegressionDataSpanSeconds { get; private set; }
        public double LargestSampleGapSeconds { get; private set; }
        public double RegressionResidualRmsNm { get; private set; }
        public int RegressionOutliersRemoved { get; private set; }
        public double ClosureConfidence => ClosureSource == "vector" ? VectorConfidence :
            ClosureSource == "vector-position-transition"
                ? System.Math.Max(PositionConfidence, VectorConfidence)
                : PositionConfidence;
        public double? ClosureDisagreementKt => PositionClosureKt.HasValue && VectorClosureKt.HasValue
            ? System.Math.Abs(PositionClosureKt.Value - VectorClosureKt.Value)
            : null;
        public double PositionConfidence => !PositionClosureKt.HasValue ? 0 :
            System.Math.Clamp(RegressionWindowSeconds / RelativeRangeHistoryWindow.TotalSeconds, 0, 1) /
            (1 + RegressionResidualRmsNm * 20 + LargestSampleGapSeconds * 0.1);
        public double VectorConfidence => VectorClosureKt.HasValue && _alignedOwnship is not null ? 0.6 : 0;

        public void Update(TrafficContactState update, OwnshipState ownship, int trailLength, TargetCategory category, IReadOnlyList<OwnshipState> ownshipHistory, DateTimeOffset snapshotTimestamp)
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

            var alignedOwnship = AlignOwnship(ownshipHistory, update.Timestamp);
            _alignedOwnship = alignedOwnship;
            _hasAlignedCurrentPosition = alignedOwnship is not null;
            OwnshipSampleAgeSeconds = System.Math.Max(0, (snapshotTimestamp - ownship.Timestamp).TotalSeconds);
            TargetSampleAgeSeconds = System.Math.Max(0, (snapshotTimestamp - update.Timestamp).TotalSeconds);
            TimestampSkewSeconds = (ownship.Timestamp - update.Timestamp).TotalSeconds;
            AlignmentMethod = GetAlignmentMethod(ownshipHistory, update.Timestamp);
            var rangeNm = alignedOwnship is null
                ? ((ownship.Timestamp - update.Timestamp).Duration() <= TimeSpan.FromSeconds(0.75)
                    ? GeoMath.DistanceNm(ownship.LatitudeDeg, ownship.LongitudeDeg, update.LatitudeDeg, update.LongitudeDeg)
                    : (double?)null)
                : GeoMath.DistanceNm(alignedOwnship.LatitudeDeg, alignedOwnship.LongitudeDeg, update.LatitudeDeg, update.LongitudeDeg);
            var identityReset = gap > ContactIdentityGap;
            var jumpReset = IsImplausibleTargetPositionJump(update, gap);
            if (identityReset || jumpReset)
            {
                if (identityReset || jumpReset)
                {
                    LastKnownCallsign = null;
                }
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
            if (rangeNm.HasValue)
            {
                _relativeRangeHistory.Add(new RelativeRangePoint(rangeNm.Value, update.Timestamp));
            }
            _relativeRangeHistory.RemoveAll(point => update.Timestamp - point.Timestamp > RelativeRangeHistoryWindow);
        }

        public TrafficContactState ResolveCallsign(TrafficContactState update)
        {
            if (update.CallsignRevoked)
            {
                LastKnownCallsign = null;
                return update with { Callsign = null, CallsignRevoked = false };
            }

            if (!string.IsNullOrWhiteSpace(update.Callsign))
            {
                LastKnownCallsign = update.Callsign.Trim().ToUpperInvariant();
                return update with { Callsign = LastKnownCallsign };
            }

            return string.IsNullOrWhiteSpace(LastKnownCallsign)
                ? update
                : update with { Callsign = LastKnownCallsign };
        }

        public void PrepareGeneration(TrafficContactState update)
        {
            if (update.Timestamp - LastUpdate <= ContactIdentityGap || update.Timestamp <= LastUpdate)
            {
                return;
            }

            LastKnownCallsign = null;
            _relativeRangeHistory.Clear();
            History.Clear();
            LastKnownClosureKt = null;
            PositionClosureKt = null;
            VectorClosureKt = null;
            ClosureSource = "unavailable";
        }

        public double? EstimateClosureKt(OwnshipState? previousOwnship, OwnshipState ownship, double bearingFromOwnshipToTargetDeg)
        {
            var previousSource = ClosureSource;
            PositionClosureKt = null;
            VectorClosureKt = null;
            PositionClosureKt = EstimatePositionClosure();
            var vectorOwnship = _alignedOwnship;
            var ownshipTrack = vectorOwnship?.GroundTrackDeg ?? vectorOwnship?.HeadingDeg;
            var targetTrack = Current.GroundTrackDeg ?? Current.HeadingDeg;
            if (vectorOwnship is not null && vectorOwnship.SpeedKt.HasValue && Current.SpeedKt.HasValue && targetTrack.HasValue && ownshipTrack.HasValue)
            {
                var alignedBearing = GeoMath.InitialBearingDeg(
                    vectorOwnship.LatitudeDeg, vectorOwnship.LongitudeDeg,
                    Current.LatitudeDeg, Current.LongitudeDeg);
                VectorClosureKt = GeoMath.RadialClosureKt(
                    ownshipTrack.Value,
                    vectorOwnship.SpeedKt.Value,
                    targetTrack.Value,
                    Current.SpeedKt.Value,
                    alignedBearing);
            }

            if (PositionClosureKt.HasValue)
            {
                if (VectorClosureKt.HasValue)
                {
                    if (previousSource == "vector" || _positionSourceTransitionStartedAt.HasValue)
                    {
                        _positionSourceTransitionStartedAt ??= Current.Timestamp;
                        var blend = System.Math.Clamp(
                            (Current.Timestamp - _positionSourceTransitionStartedAt.Value).TotalSeconds / 1.5,
                            0,
                            1);
                        LastKnownClosureKt = VectorClosureKt.Value +
                            ((PositionClosureKt.Value - VectorClosureKt.Value) * blend);
                        ClosureSource = blend < 1 ? "vector-position-transition" : "position";
                        if (blend >= 1) _positionSourceTransitionStartedAt = null;
                        return LastKnownClosureKt;
                    }
                }

                _positionSourceTransitionStartedAt = null;
                ClosureSource = "position";
                LastKnownClosureKt = PositionClosureKt;
                return PositionClosureKt;
            }

            if (VectorClosureKt.HasValue)
            {
                _positionSourceTransitionStartedAt = null;
                ClosureSource = "vector";
                LastKnownClosureKt = VectorClosureKt;
                return VectorClosureKt;
            }

            if (previousSource == "vector")
            {
                LastKnownClosureKt = null;
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
            // Small, isolated jumps are more likely a position glitch than a
            // true teleport; keep the window and let its residual filter reject it.
            if (targetDistanceNm <= 0.5)
            {
                return false;
            }

            var apparentTargetGroundSpeedKt = targetDistanceNm / gap.TotalHours;
            return apparentTargetGroundSpeedKt > MaximumPlausibleTargetGroundSpeedKt;
        }

        private double? EstimatePositionClosure()
        {
            RegressionSampleCount = 0;
            RegressionWindowSeconds = 0;
            RegressionDataSpanSeconds = 0;
            LargestSampleGapSeconds = 0;
            RegressionResidualRmsNm = 0;
            RegressionOutliersRemoved = 0;
            if (!_hasAlignedCurrentPosition || _relativeRangeHistory.Count < 3)
            {
                return null;
            }

            var latest = _relativeRangeHistory[^1].Timestamp;
            var effectiveWindow = SelectRegressionWindow(latest);
            var points = _relativeRangeHistory
                .Where(point => latest - point.Timestamp <= effectiveWindow)
                .ToArray();
            if (points.Length < 3 || latest - points[0].Timestamp < TimeSpan.FromSeconds(1.5) ||
                latest - points[^2].Timestamp > MaximumPositionClosureGap)
            {
                return null;
            }

            RegressionSampleCount = points.Length;
            RegressionWindowSeconds = effectiveWindow.TotalSeconds;
            RegressionDataSpanSeconds = (points[^1].Timestamp - points[0].Timestamp).TotalSeconds;
            LargestSampleGapSeconds = points.Zip(points.Skip(1), (first, second) => (second.Timestamp - first.Timestamp).TotalSeconds).DefaultIfEmpty(0).Max();

            var closure = FitWeightedRangeSlope(points, latest);
            if (closure is null || points.Length < 5)
            {
                return closure;
            }

            var residuals = points.Select(point => (point.Timestamp, Residual: GetRangeResidual(point, points))).ToArray();
            var absoluteResiduals = residuals.Select(static item => System.Math.Abs(item.Residual)).Order().ToArray();
            var medianResidual = absoluteResiduals[absoluteResiduals.Length / 2];
            var worst = residuals.MaxBy(static item => System.Math.Abs(item.Residual));
            var threshold = System.Math.Max(0.03, medianResidual * 4.5);
            if (System.Math.Abs(worst.Residual) > threshold)
            {
                var filtered = points.Where(point => point.Timestamp != worst.Timestamp).ToArray();
                RegressionSampleCount = filtered.Length;
                RegressionOutliersRemoved = 1;
                RegressionResidualRmsNm = System.Math.Sqrt(filtered.Average(point => System.Math.Pow(GetRangeResidual(point, filtered), 2)));
                return FitWeightedRangeSlope(filtered, latest);
            }

            RegressionResidualRmsNm = System.Math.Sqrt(points.Average(point => System.Math.Pow(GetRangeResidual(point, points), 2)));
            return closure;
        }

        private static string GetAlignmentMethod(IReadOnlyList<OwnshipState> history, DateTimeOffset timestamp)
        {
            if (history.Any(sample => sample.Timestamp == timestamp)) return "exact";
            var ordered = history.OrderBy(sample => sample.Timestamp).ToArray();
            var before = ordered.LastOrDefault(sample => sample.Timestamp < timestamp);
            var after = ordered.FirstOrDefault(sample => sample.Timestamp > timestamp);
            if (before is not null && after is not null) return "interpolation";
            var nearest = before ?? after;
            if (nearest is null) return "unavailable";
            var skew = (timestamp - nearest.Timestamp).Duration();
            if (skew <= TimeSpan.FromMilliseconds(100)) return "exact";
            return skew <= TimeSpan.FromMilliseconds(500) && nearest.SpeedKt.HasValue
                ? "extrapolation"
                : "unavailable";
        }

        private static double? FitWeightedRangeSlope(IReadOnlyList<RelativeRangePoint> points, DateTimeOffset latest)
        {
            var origin = points[0].Timestamp;
            var xs = new double[points.Count];
            var weights = new double[points.Count];
            var totalWeight = 0.0;
            var weightedXTotal = 0.0;
            var weightedRangeTotal = 0.0;
            for (var index = 0; index < points.Count; index++)
            {
                var point = points[index];
                var x = (point.Timestamp - origin).TotalHours;
                var weight = GetRangeSampleWeight(point.Timestamp, latest);
                xs[index] = x;
                weights[index] = weight;
                totalWeight += weight;
                weightedXTotal += weight * x;
                weightedRangeTotal += weight * point.RangeNm;
            }

            var meanX = weightedXTotal / totalWeight;
            var meanRange = weightedRangeTotal / totalWeight;
            var weightedCovariance = 0.0;
            var weightedTimeVariance = 0.0;
            for (var index = 0; index < points.Count; index++)
            {
                var centeredX = xs[index] - meanX;
                var centeredRange = points[index].RangeNm - meanRange;
                weightedCovariance += weights[index] * centeredX * centeredRange;
                weightedTimeVariance += weights[index] * centeredX * centeredX;
            }

            var denominator = weightedTimeVariance;
            if (denominator <= 0)
            {
                return null;
            }

            var slope = weightedCovariance / denominator;
            var closure = -slope;
            return double.IsFinite(closure) && System.Math.Abs(closure) <= MaximumPlausibleClosureKt
                ? closure
                : null;
        }

        private static double GetRangeResidual(RelativeRangePoint point, IReadOnlyList<RelativeRangePoint> points)
        {
            var origin = points[0].Timestamp;
            var meanX = points.Average(sample => (sample.Timestamp - origin).TotalHours);
            var meanY = points.Average(static sample => sample.RangeNm);
            var denominator = points.Sum(sample => System.Math.Pow((sample.Timestamp - origin).TotalHours - meanX, 2));
            if (denominator <= 0) return 0;
            var slope = points.Sum(sample =>
                ((sample.Timestamp - origin).TotalHours - meanX) * (sample.RangeNm - meanY)) / denominator;
            return point.RangeNm - (meanY + slope * ((point.Timestamp - origin).TotalHours - meanX));
        }

        private static double GetRangeSampleWeight(DateTimeOffset timestamp, DateTimeOffset latest)
        {
            var normalizedAge = (latest - timestamp).TotalSeconds / RelativeRangeHistoryWindow.TotalSeconds;
            var weightRange = MaximumRangeSampleWeight - MinimumRangeSampleWeight;
            return System.Math.Clamp(
                MaximumRangeSampleWeight - normalizedAge * weightRange,
                MinimumRangeSampleWeight,
                MaximumRangeSampleWeight);
        }

        private TimeSpan SelectRegressionWindow(DateTimeOffset latest)
        {
            var qualityWindow = TimeSpan.FromSeconds(2);
            var points = _relativeRangeHistory.Where(point => latest - point.Timestamp <= RelativeRangeHistoryWindow).ToArray();
            if (points.Length < 2 || points[^1].Timestamp - points[0].Timestamp < TimeSpan.FromSeconds(1.5))
            {
                return RelativeRangeHistoryWindow;
            }

            var largestGap = points.Zip(points.Skip(1), (a, b) => b.Timestamp - a.Timestamp).Max();
            var origin = points[0].Timestamp;
            var meanX = points.Average(point => (point.Timestamp - origin).TotalSeconds);
            var meanY = points.Average(static point => point.RangeNm);
            var denominator = points.Sum(point => System.Math.Pow((point.Timestamp - origin).TotalSeconds - meanX, 2));
            var slope = denominator <= 0 ? 0 : points.Sum(point =>
                ((point.Timestamp - origin).TotalSeconds - meanX) * (point.RangeNm - meanY)) / denominator;
            var intercept = meanY - slope * meanX;
            var jitter = System.Math.Sqrt(points.Average(point =>
                System.Math.Pow(point.RangeNm - (intercept + slope * (point.Timestamp - origin).TotalSeconds), 2)));
            return largestGap <= TimeSpan.FromSeconds(1) && jitter <= 0.04
                ? qualityWindow
                : RelativeRangeHistoryWindow;
        }

        private sealed record RelativeRangePoint(double RangeNm, DateTimeOffset Timestamp);
    }

    private static OwnshipState? AlignOwnship(IReadOnlyList<OwnshipState> history, DateTimeOffset targetTime)
    {
        if (history.Count == 0)
        {
            return null;
        }

        var ordered = history.OrderBy(sample => sample.Timestamp).ToArray();
        var before = ordered.LastOrDefault(sample => sample.Timestamp <= targetTime);
        var after = ordered.FirstOrDefault(sample => sample.Timestamp >= targetTime);
        if (before is not null && after is not null)
        {
            var span = (after.Timestamp - before.Timestamp).TotalSeconds;
            if (span == 0) return before;
            var f = (targetTime - before.Timestamp).TotalSeconds / span;
            var ownshipDistanceNm = GeoMath.DistanceNm(before.LatitudeDeg, before.LongitudeDeg, after.LatitudeDeg, after.LongitudeDeg);
            var ownshipTrack = GeoMath.InitialBearingDeg(before.LatitudeDeg, before.LongitudeDeg, after.LatitudeDeg, after.LongitudeDeg);
            var interpolatedPosition = GeoMath.DestinationPoint(before.LatitudeDeg, before.LongitudeDeg, ownshipTrack, ownshipDistanceNm * f);
            return before with
            {
                LatitudeDeg = interpolatedPosition.latitudeDeg,
                LongitudeDeg = interpolatedPosition.longitudeDeg,
                AltitudeFt = before.AltitudeFt + ((after.AltitudeFt - before.AltitudeFt) * f),
                HeadingDeg = GeoMath.NormalizeDegrees(before.HeadingDeg + GeoMath.SignedRelativeBearingDeg(before.HeadingDeg, after.HeadingDeg) * f),
                SpeedKt = before.SpeedKt.HasValue && after.SpeedKt.HasValue ? before.SpeedKt + ((after.SpeedKt - before.SpeedKt) * f) : null,
                GroundTrackDeg = before.GroundTrackDeg.HasValue && after.GroundTrackDeg.HasValue
                    ? GeoMath.NormalizeDegrees(before.GroundTrackDeg.Value + GeoMath.SignedRelativeBearingDeg(before.GroundTrackDeg.Value, after.GroundTrackDeg.Value) * f)
                    : null,
                Timestamp = targetTime
            };
        }

        var nearest = before ?? after;
        if (nearest is null)
        {
            return null;
        }

        var skew = (targetTime - nearest.Timestamp).TotalSeconds;
        if (System.Math.Abs(skew) <= 0.1)
        {
            return nearest with { Timestamp = targetTime };
        }

        if (System.Math.Abs(skew) > 0.5 || !nearest.SpeedKt.HasValue)
        {
            return null;
        }

        var track = nearest.GroundTrackDeg ?? nearest.HeadingDeg;
        var bearing = skew < 0 ? GeoMath.NormalizeDegrees(track + 180) : track;
        var distance = nearest.SpeedKt.Value * System.Math.Abs(skew) / 3600.0;
        var projected = GeoMath.DestinationPoint(nearest.LatitudeDeg, nearest.LongitudeDeg, bearing, distance);
        return nearest with
        {
            LatitudeDeg = projected.latitudeDeg,
            LongitudeDeg = projected.longitudeDeg,
            Timestamp = targetTime
        };
    }

    private sealed record SuppressedContact(
        TrafficContactState Contact,
        DateTimeOffset FirstSeen,
        DateTimeOffset LastSeen);
}

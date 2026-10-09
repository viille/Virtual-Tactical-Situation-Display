using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using TacticalDisplay.App.Services;
using TacticalDisplay.Core.Models;
using TacticalDisplay.Core.Services;

namespace TacticalDisplay.App.Data;

public sealed class VatsimCallsignTrafficFeed : ITrafficDataFeed, IAarPoseSource, IAarFuelAdapter, IAarBridgeRuntimeStatusSource
{
    private const string LogSource = "VATSIM";
    private const int RequiredStableCallsignMatches = 2;
    private const int RequiredStableFallbackCallsignMatches = 3;
    private static readonly TimeSpan RequiredFallbackObservationWindow = TimeSpan.FromSeconds(2);
    private const int RequiredStableCallsignSwitchMatches = 3;
    private const double StrongSwitchMaxDistanceNm = 0.75;
    private const double StrongSwitchMaxAltitudeDeltaFt = 400;
    private static readonly TimeSpan SnapshotHistoryRetention = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan ConfirmedCallsignEvidenceRetention = TimeSpan.FromSeconds(30);

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly ITrafficDataFeed _inner;
    private readonly TacticalDisplaySettings _settings;
    private readonly HttpClient _httpClient;
    private readonly Func<VatsimOwnshipIdentity?> _getOwnshipIdentity;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private readonly object _historyLock = new();
    private readonly object _pendingSnapshotLock = new();
    private readonly Queue<TrafficSnapshot> _snapshotHistory = new();
    private readonly Dictionary<string, CallsignConfirmation> _callsignConfirmations = new(StringComparer.OrdinalIgnoreCase);
    private readonly CallsignPublicationOwnership _callSignOwnership = new();
    private readonly object _publicationLock = new();
    private IReadOnlyList<VatsimPilotCandidate> _cachedPilots = [];
    private bool _hasPilotFeedSnapshot;
    private DateTimeOffset _lastRefreshAt = DateTimeOffset.MinValue;
    private bool _isEnriching;
    private TrafficSnapshot? _pendingSnapshot;
    private long _connectionEpoch;
    private long _lastObservedConnectionEpoch;
    private string _lastIdentityKey = string.Empty;
    private VatsimOwnshipIdentity? _effectiveOwnshipIdentity;
    private bool _identityStateResetPending;

    public VatsimCallsignTrafficFeed(
        ITrafficDataFeed inner,
        TacticalDisplaySettings settings,
        Func<VatsimOwnshipIdentity?>? getOwnshipIdentity = null,
        HttpClient? httpClient = null)
    {
        _inner = inner;
        _settings = settings;
        _getOwnshipIdentity = getOwnshipIdentity ?? (() => new VatsimOwnshipIdentity(null, settings.OwnCallsign));
        _httpClient = httpClient ?? new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(6)
        };
        _httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("Tactical-Situation-Display");
        _inner.SnapshotReceived += OnInnerSnapshotReceived;
        _inner.ConnectionChanged += OnInnerConnectionChanged;
        if (_inner is IAarPoseSource poseSource) poseSource.AarPoseSampled += OnAarPoseSampled;
        if (_inner is IAarFuelAdapter fuelAdapter) fuelAdapter.FuelSampled += OnFuelSampled;
        if (_inner is IAarBridgeRuntimeStatusSource bridgeStatus) bridgeStatus.BridgeRuntimeStateChanged += OnBridgeRuntimeStateChanged;
        DataSourceDebugLog.Info(LogSource, $"VATSIM callsign lookup enabled | feed={GetFeedUri()} refreshSeconds={Math.Clamp(_settings.VatsimCallsignRefreshSeconds, 15, 300):0}");
    }

    public event EventHandler<TrafficSnapshot>? SnapshotReceived;
    public event EventHandler<bool>? ConnectionChanged;
    public event EventHandler<OwnshipState>? AarPoseSampled;
    public event EventHandler<AarFuelReading>? FuelSampled;
    public event EventHandler<AarBridgeRuntimeState>? BridgeRuntimeStateChanged;
    public AarBridgeRuntimeState? BridgeRuntimeState => (_inner as IAarBridgeRuntimeStatusSource)?.BridgeRuntimeState;
    public string? BridgeVersion => (_inner as IAarBridgeRuntimeStatusSource)?.BridgeVersion;
    public string? BridgeDiagnostic => (_inner as IAarBridgeRuntimeStatusSource)?.BridgeDiagnostic;
    public bool AarSamplingEnabled
    {
        get => _inner is IAarPoseSource poseSource && poseSource.AarSamplingEnabled;
        set { if (_inner is IAarPoseSource poseSource) poseSource.AarSamplingEnabled = value; }
    }
    public bool IsConnected => _inner.IsConnected;
    public bool IsAvailable => _inner is IAarFuelAdapter adapter && adapter.IsAvailable;
    public bool CanReadFuel => _inner is IAarFuelAdapter adapter && adapter.CanReadFuel;
    public bool CanWriteFuel => _inner is IAarFuelAdapter adapter && adapter.CanWriteFuel;
    public AarFuelReading? ReadFuel() => _inner is IAarFuelAdapter adapter ? adapter.ReadFuel() : null;
    public Task<AarFuelApplyResult> ApplyFuelDeltaKgAsync(double deltaKg, CancellationToken cancellationToken) => _inner is IAarFuelAdapter adapter
        ? adapter.ApplyFuelDeltaKgAsync(deltaKg, cancellationToken)
        : Task.FromResult(new AarFuelApplyResult(deltaKg, 0, AarFuelApplyStatus.Failed, "The selected simulator feed has no AAR fuel adapter."));

    public Task StartAsync(CancellationToken cancellationToken) =>
        _inner.StartAsync(cancellationToken);

    public Task StopAsync() =>
        _inner.StopAsync();

    public async ValueTask DisposeAsync()
    {
        _inner.SnapshotReceived -= OnInnerSnapshotReceived;
        _inner.ConnectionChanged -= OnInnerConnectionChanged;
        if (_inner is IAarPoseSource poseSource) poseSource.AarPoseSampled -= OnAarPoseSampled;
        if (_inner is IAarFuelAdapter fuelAdapter) fuelAdapter.FuelSampled -= OnFuelSampled;
        if (_inner is IAarBridgeRuntimeStatusSource bridgeStatus) bridgeStatus.BridgeRuntimeStateChanged -= OnBridgeRuntimeStateChanged;
        await _inner.DisposeAsync();
        _refreshLock.Dispose();
        _httpClient.Dispose();
    }

    private void OnInnerConnectionChanged(object? sender, bool connected)
    {
        long connectionEpoch;
        if (!connected)
        {
            lock (_pendingSnapshotLock)
            {
                _connectionEpoch++;
                connectionEpoch = _connectionEpoch;
                _pendingSnapshot = null;
            }

            lock (_publicationLock) _callSignOwnership.Clear();
        }
        else
        {
            lock (_pendingSnapshotLock) connectionEpoch = _connectionEpoch;
        }

        DataSourceDebugLog.ThrottledDebug(LogSource, $"connection-{connected}-{connectionEpoch}", TimeSpan.FromSeconds(1),
            () => $"Callsign feed connection changed | connected={connected} connectionEpoch={connectionEpoch} ownershipCleared={!connected}");

        ConnectionChanged?.Invoke(sender, connected);
    }

    private void OnAarPoseSampled(object? sender, OwnshipState sample) => AarPoseSampled?.Invoke(this, sample);
    private void OnFuelSampled(object? sender, AarFuelReading sample) => FuelSampled?.Invoke(this, sample);
    private void OnBridgeRuntimeStateChanged(object? sender, AarBridgeRuntimeState state) => BridgeRuntimeStateChanged?.Invoke(this, state);

    private void OnInnerSnapshotReceived(object? sender, TrafficSnapshot snapshot)
    {
        RememberSnapshot(snapshot);
        lock (_pendingSnapshotLock)
        {
            _pendingSnapshot = snapshot;
            if (_isEnriching) return;
            _isEnriching = true;
        }

        _ = EnrichPendingSnapshotsAsync();
    }

    private async Task EnrichPendingSnapshotsAsync()
    {
        while (true)
        {
            TrafficSnapshot? snapshot;
            lock (_pendingSnapshotLock)
            {
                snapshot = _pendingSnapshot;
                _pendingSnapshot = null;
                if (snapshot is null)
                {
                    _isEnriching = false;
                    return;
                }
            }

            await EnrichAndPublishOneAsync(snapshot).ConfigureAwait(false);
        }
    }

    private async Task EnrichAndPublishOneAsync(TrafficSnapshot snapshot)
    {
        long connectionEpoch;
        lock (_pendingSnapshotLock) connectionEpoch = _connectionEpoch;
        if (connectionEpoch != _lastObservedConnectionEpoch)
        {
            _callsignConfirmations.Clear();
            _lastObservedConnectionEpoch = connectionEpoch;
        }

        try
        {
            var pilots = await GetPilotsAsync(CancellationToken.None).ConfigureAwait(false);
            if (connectionEpoch != GetConnectionEpoch())
            {
                _callsignConfirmations.Clear();
                return;
            }
            if (HasPendingSnapshot()) return;
            var history = GetSnapshotHistory();
            var identity = _getOwnshipIdentity();
            var ownPilot = !string.IsNullOrWhiteSpace(identity?.Cid)
                ? pilots.FirstOrDefault(pilot => string.Equals(pilot.Cid, identity.Cid, StringComparison.OrdinalIgnoreCase))
                : null;
            if (ownPilot is not null && string.IsNullOrWhiteSpace(identity?.Callsign))
            {
                identity = identity! with { Callsign = ownPilot.Callsign };
            }
            ResetIdentityStateIfChanged(identity);
            var allPilots = pilots;
            pilots = ExcludeOwnshipPilots(pilots, identity);
            var reservedCallsigns = snapshot.Contacts
                .Where(contact => contact.Source == TrackSource.TacticalLink && !string.IsNullOrWhiteSpace(contact.Callsign))
                .Select(contact => contact.Callsign!)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            pilots = pilots.Where(pilot => !reservedCallsigns.Contains(pilot.Callsign)).ToArray();
            var proposed = VatsimCallsignMatcher.EnrichSnapshotFromHistory(snapshot, history, pilots, identity);
            var confirmationStateBeforeSnapshot = new Dictionary<string, CallsignConfirmation>(_callsignConfirmations, StringComparer.OrdinalIgnoreCase);
            var confirmed = ConfirmCallsignMatches(snapshot, proposed, history, pilots);
            TrafficSnapshot published;
            lock (_pendingSnapshotLock)
            {
                if (_pendingSnapshot is not null)
                {
                    _callsignConfirmations.Clear();
                    foreach (var pair in confirmationStateBeforeSnapshot) _callsignConfirmations[pair.Key] = pair.Value;
                    return;
                }

                if (connectionEpoch != _connectionEpoch)
                {
                    _callsignConfirmations.Clear();
                    _lastObservedConnectionEpoch = _connectionEpoch;
                    return;
                }

                published = ReconcilePublication(confirmed, identity);
                SnapshotReceived?.Invoke(this, published);
            }

            LogEnrichmentSummary(snapshot, published, pilots);
        }
        catch (Exception ex)
        {
            DataSourceDebugLog.ThrottledDebug(
                LogSource,
                "callsign-enrichment-failed",
                TimeSpan.FromSeconds(30),
                () => $"Callsign lookup failed; publishing simulator snapshot unchanged | error={ex.Message}");
            lock (_pendingSnapshotLock)
            {
                if (_pendingSnapshot is null && connectionEpoch == _connectionEpoch)
                {
                    SnapshotReceived?.Invoke(this, ReconcilePublication(snapshot, GetPublicationIdentity()));
                }
                else if (connectionEpoch != _connectionEpoch)
                {
                    _callsignConfirmations.Clear();
                    _lastObservedConnectionEpoch = _connectionEpoch;
                }
            }
        }
    }

    private bool HasPendingSnapshot()
    {
        lock (_pendingSnapshotLock) return _pendingSnapshot is not null;
    }

    private long GetConnectionEpoch()
    {
        lock (_pendingSnapshotLock) return _connectionEpoch;
    }

    private static IReadOnlyList<VatsimPilotCandidate> ExcludeOwnshipPilots(
        IReadOnlyList<VatsimPilotCandidate> pilots,
        VatsimOwnshipIdentity? identity) => identity is null ? pilots : pilots.Where(pilot =>
            !(!string.IsNullOrWhiteSpace(identity.Cid)
                ? string.Equals(pilot.Cid, identity.Cid, StringComparison.OrdinalIgnoreCase)
                : !string.IsNullOrWhiteSpace(identity.Callsign) && string.Equals(pilot.Callsign, identity.Callsign, StringComparison.OrdinalIgnoreCase))).ToArray();

    private void ResetIdentityStateIfChanged(VatsimOwnshipIdentity? identity)
    {
        var identityKey = $"{identity?.Cid}|{identity?.Callsign}";
        if (string.Equals(identityKey, _lastIdentityKey, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _callsignConfirmations.Clear();
        _identityStateResetPending = true;
        var previousIdentityKey = _lastIdentityKey;
        lock (_publicationLock)
        {
            _callSignOwnership.Clear();
            _lastIdentityKey = identityKey;
            _effectiveOwnshipIdentity = identity;
        }
        DataSourceDebugLog.ThrottledDebug(LogSource, $"identity-reset-{identityKey}", TimeSpan.FromSeconds(1),
            () => $"Callsign ownership reset after ownship identity change | previousIdentity={previousIdentityKey} currentIdentity={identityKey}");
    }

    private VatsimOwnshipIdentity? GetPublicationIdentity()
    {
        var configured = _getOwnshipIdentity();
        lock (_publicationLock)
        {
            return !string.IsNullOrWhiteSpace(configured?.Cid) &&
                string.Equals(configured.Cid, _effectiveOwnshipIdentity?.Cid, StringComparison.OrdinalIgnoreCase) &&
                string.IsNullOrWhiteSpace(configured.Callsign) &&
                !string.IsNullOrWhiteSpace(_effectiveOwnshipIdentity?.Callsign)
                ? _effectiveOwnshipIdentity
                : configured;
        }
    }

    private TrafficSnapshot ReconcilePublication(TrafficSnapshot snapshot, VatsimOwnshipIdentity? identity)
    {
        lock (_publicationLock)
        {
            if (_identityStateResetPending)
            {
                snapshot = snapshot with
                {
                    Contacts = snapshot.Contacts.Select(contact => string.IsNullOrWhiteSpace(contact.Callsign)
                        ? contact with { CallsignRevoked = true }
                        : contact).ToArray()
                };
                _identityStateResetPending = false;
            }

            return _callSignOwnership.Reconcile(snapshot, identity?.Callsign, ConfirmedCallsignEvidenceRetention);
        }
    }

    private async Task<IReadOnlyList<VatsimPilotCandidate>> GetPilotsAsync(CancellationToken cancellationToken)
    {
        var refreshInterval = TimeSpan.FromSeconds(Math.Clamp(_settings.VatsimCallsignRefreshSeconds, 15, 300));
        var now = DateTimeOffset.UtcNow;
        if (_hasPilotFeedSnapshot && now - _lastRefreshAt < refreshInterval)
        {
            return _cachedPilots;
        }

        await _refreshLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            now = DateTimeOffset.UtcNow;
            if (_hasPilotFeedSnapshot && now - _lastRefreshAt < refreshInterval)
            {
                return _cachedPilots;
            }

            using var response = await _httpClient.GetAsync(GetFeedUri(), cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            var feed = await JsonSerializer.DeserializeAsync<VatsimDataFeed>(stream, JsonOptions, cancellationToken).ConfigureAwait(false);
            if (feed?.Pilots is null)
            {
                throw new JsonException("VATSIM feed did not contain a pilots array.");
            }

            _cachedPilots = feed.Pilots
                .Where(static pilot => !string.IsNullOrWhiteSpace(pilot.Callsign))
                .Select(pilot => new VatsimPilotCandidate(
                    pilot.Callsign.Trim().ToUpperInvariant(),
                    pilot.Latitude,
                    pilot.Longitude,
                    pilot.Altitude,
                    pilot.Groundspeed,
                    pilot.Heading,
                    pilot.LastUpdated ?? feed.General?.UpdateTimestamp,
                    ReadCid(pilot.Cid)))
                .ToList() ?? [];
            _hasPilotFeedSnapshot = true;
            _lastRefreshAt = now;

            DataSourceDebugLog.ThrottledDebug(
                LogSource,
                "callsign-feed-refresh",
                TimeSpan.FromMinutes(1),
                () => $"VATSIM pilot feed refreshed | pilots={_cachedPilots.Count}");

            return _cachedPilots;
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    private void RememberSnapshot(TrafficSnapshot snapshot)
    {
        lock (_historyLock)
        {
            _snapshotHistory.Enqueue(snapshot);
            var cutoff = snapshot.Timestamp - SnapshotHistoryRetention;
            while (_snapshotHistory.Count > 0 && _snapshotHistory.Peek().Timestamp < cutoff)
            {
                _snapshotHistory.Dequeue();
            }
        }
    }

    private IReadOnlyList<TrafficSnapshot> GetSnapshotHistory()
    {
        lock (_historyLock)
        {
            return _snapshotHistory.ToArray();
        }
    }

    private TrafficSnapshot ConfirmCallsignMatches(
        TrafficSnapshot original,
        TrafficSnapshot enriched,
        IReadOnlyList<TrafficSnapshot> history,
        IReadOnlyList<VatsimPilotCandidate> pilots)
    {
        var activeContactIds = enriched.Contacts
            .Select(ContactIdentityKey)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var contactId in _callsignConfirmations.Keys.Except(activeContactIds, StringComparer.OrdinalIgnoreCase).ToList())
        {
            _callsignConfirmations.Remove(contactId);
        }

        var pilotUpdatesByCallsign = pilots
            .Where(static pilot => !string.IsNullOrWhiteSpace(pilot.Callsign))
            .GroupBy(static pilot => pilot.Callsign.Trim().ToUpperInvariant(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                static group => group.Key,
                static group => group
                    .Select(static pilot => pilot.LastUpdated)
                    .Where(static lastUpdated => lastUpdated.HasValue)
                    .Max(),
                StringComparer.OrdinalIgnoreCase);

        var confirmedContacts = new List<TrafficContactState>(enriched.Contacts.Count);
        foreach (var pair in original.Contacts.Zip(enriched.Contacts))
        {
            var originalContact = pair.First;
            var enrichedContact = pair.Second;
            if (!string.IsNullOrWhiteSpace(originalContact.Callsign))
            {
                confirmedContacts.Add(enrichedContact);
                continue;
            }

            if (string.IsNullOrWhiteSpace(enrichedContact.Callsign))
            {
                var contactIdentity = ContactIdentityKey(enrichedContact);
                if (_callsignConfirmations.TryGetValue(contactIdentity, out var previous) &&
                    previous.ConfirmedCallsign is not null && previous.LastObservationTime.HasValue)
                {
                    if (original.Timestamp - previous.LastObservationTime.Value > ConfirmedCallsignEvidenceRetention)
                    {
                        _callsignConfirmations.Remove(contactIdentity);
                        confirmedContacts.Add(enrichedContact with { Callsign = null, CallsignRevoked = true });
                    }
                    else
                    {
                        confirmedContacts.Add(enrichedContact with { Callsign = previous.ConfirmedCallsign });
                    }
                }
                else
                {
                    confirmedContacts.Add(enrichedContact);
                }

                continue;
            }

            var callsign = enrichedContact.Callsign.Trim().ToUpperInvariant();
            pilotUpdatesByCallsign.TryGetValue(callsign, out var pilotUpdateTime);
            var pilot = pilots.FirstOrDefault(candidate =>
                string.Equals(candidate.Callsign, callsign, StringComparison.OrdinalIgnoreCase));
            var matchDiagnostics = pilot is null
                ? VatsimMatchDiagnostics.None
                : VatsimCallsignMatcher.InspectMatch(originalContact, pilot);
            if (!matchDiagnostics.IsMatch && pilot is not null)
            {
                // The simulator and VATSIM timestamps/positions can be a few
                // seconds apart. Reuse the same historical evidence that
                // produced the assignment before counting a confirmation.
                matchDiagnostics = VatsimCallsignMatcher.InspectBestHistoricalMatch(
                    originalContact,
                    history,
                    [pilot]);
            }
            if (!matchDiagnostics.IsMatch)
            {
                // Never display an unconfirmed candidate merely because the
                // assignment was produced from a stale or transient sample.
                confirmedContacts.Add(enrichedContact with { Callsign = null });
                continue;
            }
            var confirmation = UpdateCallsignConfirmation(
                ContactIdentityKey(enrichedContact),
                callsign,
                pilotUpdateTime,
                originalContact.Timestamp,
                IsStrongSwitchMatch(matchDiagnostics),
                matchDiagnostics.IsFallback);
            DataSourceDebugLog.ThrottledDebug(
                LogSource,
                $"callsign-confirmation-{enrichedContact.Id}",
                TimeSpan.FromSeconds(5),
                () =>
                    "Callsign confirmation | " +
                    $"contact={enrichedContact.Id} callsign={callsign} count={confirmation.CandidateMatchCount} " +
                    $"confirmed={confirmation.Confirmed} pilotUpdated={pilotUpdateTime?.ToString("O") ?? "n/a"}");

            if (confirmation.Confirmed)
            {
                confirmedContacts.Add(enrichedContact with { Callsign = confirmation.ConfirmedCallsign });
                continue;
            }

            confirmedContacts.Add(enrichedContact with { Callsign = confirmation.ConfirmedCallsign });
        }

        return enriched with
        {
            Contacts = confirmedContacts.Select(contact => contact with
            {
                Source = contact.Source == TrackSource.TacticalLink
                    ? TrackSource.TacticalLink
                    : string.IsNullOrWhiteSpace(contact.Callsign) ? TrackSource.SimConnect : TrackSource.SimConnectVatsim
            }).ToArray()
        };
    }

    private CallsignConfirmation UpdateCallsignConfirmation(
        string contactId,
        string callsign,
        DateTimeOffset? pilotUpdateTime,
        DateTimeOffset observationTime,
        bool strongMatch,
        bool fallbackMatch)
    {
        if (!_callsignConfirmations.TryGetValue(contactId, out var confirmation))
        {
            confirmation = new CallsignConfirmation(callsign, 0, null, null, null, false, null);
        }
        else if (!string.Equals(confirmation.CandidateCallsign, callsign, StringComparison.OrdinalIgnoreCase))
        {
            confirmation = confirmation with
            {
                CandidateCallsign = callsign,
                CandidateMatchCount = 0,
                CandidateFirstObservationTime = null
            };
        }

        var isSwitchCandidate = confirmation.ConfirmedCallsign is not null &&
            !string.Equals(confirmation.ConfirmedCallsign, callsign, StringComparison.OrdinalIgnoreCase);
        var count = isSwitchCandidate && !strongMatch
            ? 0
            : confirmation.CandidateMatchCount;
        // VATSIM's last_updated value can remain unchanged while the simulator
        // emits several new observations. Confirmation must follow observations,
        // otherwise a valid match remains unconfirmed forever and stationary
        // contacts are later removed by TrafficRepository.
        if (confirmation.LastObservationTime is null ||
            observationTime > confirmation.LastObservationTime.Value)
        {
            count++;
        }
        var firstObservationTime = confirmation.CandidateFirstObservationTime ?? observationTime;

        var confirmedCallsign = confirmation.ConfirmedCallsign;
        // A very close direct match is safe enough to show immediately. This
        // improves visibility for short-lived or stationary contacts while
        // ordinary and historical matches still require two observations.
        if (confirmedCallsign is null && strongMatch)
        {
            confirmedCallsign = callsign;
        }
        else if (confirmedCallsign is null &&
            count >= RequiredStableCallsignMatches &&
            (!fallbackMatch ||
                (count >= RequiredStableFallbackCallsignMatches &&
                    observationTime - firstObservationTime >= RequiredFallbackObservationWindow)))
        {
            confirmedCallsign = callsign;
        }
        else if (confirmedCallsign is not null &&
            !string.Equals(confirmedCallsign, callsign, StringComparison.OrdinalIgnoreCase) &&
            strongMatch &&
            count >= RequiredStableCallsignSwitchMatches)
        {
            confirmedCallsign = callsign;
        }

        confirmation = confirmation with
        {
            CandidateMatchCount = count,
            LastPilotUpdateTime = pilotUpdateTime ?? confirmation.LastPilotUpdateTime,
            LastObservationTime = observationTime,
            CandidateFirstObservationTime = firstObservationTime,
            PreviousConfirmedCallsign = confirmation.ConfirmedCallsign,
            ConfirmedCallsign = confirmedCallsign,
            Confirmed = confirmedCallsign is not null
        };
        _callsignConfirmations[contactId] = confirmation;
        return confirmation;
    }

    private static bool IsStrongSwitchMatch(VatsimMatchDiagnostics diagnostics) =>
        diagnostics.IsMatch &&
        diagnostics.DistanceNm <= StrongSwitchMaxDistanceNm &&
        diagnostics.AltitudeDeltaFt <= StrongSwitchMaxAltitudeDeltaFt;

    private static string ContactIdentityKey(TrafficContactState contact) =>
        $"{contact.Id}@{contact.Generation}";

    private void LogCallsignPipelineDiagnostics(
        TrafficSnapshot raw,
        TrafficSnapshot proposed,
        TrafficSnapshot confirmed,
        TrafficSnapshot published,
        IReadOnlyList<TrafficSnapshot> history,
        IReadOnlyList<VatsimPilotCandidate> candidates,
        IReadOnlyList<VatsimPilotCandidate> allPilots,
        VatsimOwnshipIdentity? identity,
        IReadOnlyDictionary<string, CallsignConfirmation> confirmations)
    {
        var assignmentDiagnostics = VatsimCallsignMatcher.InspectCurrentAssignment(raw.Contacts, candidates);
        foreach (var contact in raw.Contacts.Take(12))
        {
            var key = ContactIdentityKey(contact);
            confirmations.TryGetValue(key, out var state);
            var proposedCallsign = proposed.Contacts.FirstOrDefault(item => ContactIdentityKey(item) == key)?.Callsign;
            var confirmedCallsign = confirmed.Contacts.FirstOrDefault(item => ContactIdentityKey(item) == key)?.Callsign;
            var publishedCallsign = published.Contacts.FirstOrDefault(item => ContactIdentityKey(item) == key)?.Callsign;
            var ranked = candidates.Select(pilot => VatsimCallsignMatcher.InspectMatch(contact, pilot))
                .OrderBy(match => match.Score).ToArray();
            var best = ranked.FirstOrDefault() ?? VatsimMatchDiagnostics.None;
            var historical = VatsimCallsignMatcher.InspectBestHistoricalMatch(contact, history, candidates);
            var proposedPilot = proposedCallsign is null ? null : candidates.FirstOrDefault(pilot =>
                string.Equals(pilot.Callsign, proposedCallsign, StringComparison.OrdinalIgnoreCase));
            var proposedDiagnostic = proposedPilot is null
                ? VatsimMatchDiagnostics.None
                : VatsimCallsignMatcher.InspectMatch(contact, proposedPilot);
            var secondScore = proposedCallsign is null
                ? ranked.Skip(1).Select(match => (double?)match.Score).FirstOrDefault()
                : ranked.Where(match => !string.Equals(match.Callsign, proposedCallsign, StringComparison.OrdinalIgnoreCase))
                    .Select(match => (double?)match.Score).FirstOrDefault();
            var candidateScore = proposedCallsign is null ? best.Score : proposedDiagnostic.Score;
            var source = proposedCallsign is null ? "none" :
                proposedDiagnostic.IsMatch ? (proposedDiagnostic.IsFallback ? "fallback" : "current") : "historical";
            var ownership = publishedCallsign is not null ? "published-owner" :
                confirmedCallsign is not null ? "blocked-or-revoked" : "unowned";
            var reason = publishedCallsign is not null ? "accepted" :
                proposedCallsign is null && best.IsMatch ? "ambiguous-or-assignment-conflict" :
                proposedCallsign is null ? best.RejectReason ?? "no-candidate" :
                confirmedCallsign is null ? "awaiting-confirmation" : "ownership-or-publication-rejected";
            var ownExcluded = allPilots.Count - candidates.Count;
            var ownshipReason = ownExcluded > 0
                ? $"cid={identity?.Cid ?? "n/a"};callsign={identity?.Callsign ?? "n/a"}"
                : "none";
            var margin = secondScore.HasValue ? secondScore.Value - candidateScore : double.NaN;
            assignmentDiagnostics.TryGetValue(contact.Id, out var assignment);
            var candidateSummary = string.Join(",", ranked.Take(4).Select(match =>
                $"{match.Callsign}:{match.Score.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture)}:{(match.IsMatch ? "candidate" : match.RejectReason ?? "rejected")}"));
            var publishedContact = published.Contacts.FirstOrDefault(item => ContactIdentityKey(item) == key);
            var line = $"Callsign pipeline | contact={contact.Id} generation={contact.Generation} proposed={proposedCallsign ?? "---"} candidateScore={candidateScore:0.000} " +
                $"secondBestScore={secondScore?.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture) ?? "---"} margin={margin:0.000} source={source} " +
                $"candidateCount={ranked.Count(match => match.IsMatch)} candidateAlternatives={candidateSummary} " +
                $"assignmentComponent={assignment?.ComponentId.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "---"} " +
                $"assignmentComponentContacts={assignment?.ComponentContactCount.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "---"} " +
                $"currentAssignmentStable={assignment?.StableAcrossPlausibleSolutions.ToString() ?? "unknown"} " +
                $"unmatchedAlternativeCost={assignment?.UnmatchedAlternativeCost.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture) ?? "---"} " +
                $"ambiguityReason={assignment?.AmbiguityReason ?? "not-in-current-candidate-graph"} " +
                $"confirmation={(state?.Confirmed == true ? "confirmed" : "pending")} previousConfirmed={state?.PreviousConfirmedCallsign ?? "---"} " +
                $"confirmed={state?.ConfirmedCallsign ?? "---"} ownership={ownership} published={publishedCallsign ?? "---"} rejectReason={reason} " +
                $"revoked={publishedContact?.CallsignRevoked ?? raw.Contacts.FirstOrDefault(item => ContactIdentityKey(item) == key)?.CallsignRevoked ?? false} " +
                $"ownshipExcluded={ownExcluded} ownshipReason={ownshipReason} historicalCandidate={historical.Callsign ?? "---"} " +
                $"decision={(publishedCallsign is null ? "rejected-or-unresolved" : "accepted-and-published")}";
            DataSourceDebugLog.ThrottledDebug(LogSource, $"callsign-pipeline-{key}", TimeSpan.FromSeconds(5), () => line);
        }
    }

    private Uri GetFeedUri()
    {
        if (Uri.TryCreate(_settings.VatsimDataFeedUrl, UriKind.Absolute, out var uri) &&
            uri.Scheme is "http" or "https")
        {
            return uri;
        }

        return new Uri("https://data.vatsim.net/v3/vatsim-data.json");
    }

    private static string? ReadCid(JsonElement? cid) => cid?.ValueKind switch
    {
        JsonValueKind.String => cid.Value.GetString(),
        JsonValueKind.Number => cid.Value.GetRawText(),
        _ => null
    };

    private static void LogEnrichmentSummary(
        TrafficSnapshot original,
        TrafficSnapshot enriched,
        IReadOnlyList<VatsimPilotCandidate> pilots)
    {
        DataSourceDebugLog.ThrottledDebug(
            LogSource,
            "callsign-enrichment-summary",
            TimeSpan.FromSeconds(10),
            () =>
            {
                var added = original.Contacts
                    .Zip(enriched.Contacts)
                    .Count(pair =>
                        string.IsNullOrWhiteSpace(pair.First.Callsign) &&
                        !string.IsNullOrWhiteSpace(pair.Second.Callsign));
                var missing = enriched.Contacts.Count(contact => string.IsNullOrWhiteSpace(contact.Callsign));
                return "Callsign enrichment summary | " +
                    $"contacts={original.Contacts.Count} pilots={pilots.Count} added={added} missing={missing}";
            });
    }

    private static void LogCallsignMatchDiagnostics(
        TrafficSnapshot snapshot,
        IReadOnlyList<TrafficSnapshot> history,
        IReadOnlyList<VatsimPilotCandidate> pilots)
    {
        DataSourceDebugLog.ThrottledDebug(
            LogSource,
            "callsign-match-diagnostics",
            TimeSpan.FromSeconds(5),
            () =>
            {
                var unresolved = snapshot.Contacts
                    .Where(static contact => string.IsNullOrWhiteSpace(contact.Callsign))
                    .Take(8)
                    .Select(contact =>
                    {
                        var currentNearest = VatsimCallsignMatcher.InspectBestMatch(contact, pilots);
                        var historicalNearest = VatsimCallsignMatcher.InspectBestHistoricalMatch(contact, history, pilots);
                        return
                            $"contact={contact.Id} " +
                            $"currentNearest={currentNearest.Callsign ?? "n/a"} currentMatch={currentNearest.IsMatch} " +
                            $"currentReject={currentNearest.RejectReason ?? "none"} currentDistanceNm={currentNearest.DistanceNm:0.00} " +
                            $"historicalNearest={historicalNearest.Callsign ?? "n/a"} historicalMatch={historicalNearest.IsMatch} " +
                            $"historicalReject={historicalNearest.RejectReason ?? "none"} historicalDistanceNm={historicalNearest.DistanceNm:0.00} " +
                            $"historicalAltitudeDeltaFt={historicalNearest.AltitudeDeltaFt:0} historicalScore={historicalNearest.Score:0.00}";
                    })
                    .ToList();
                var historyText = "historyCount=0 historySpanSeconds=0";
                if (history.Count > 0)
                {
                    var historyStart = history.Min(static item => item.Timestamp);
                    var historyEnd = history.Max(static item => item.Timestamp);
                    historyText = $"historyCount={history.Count} historySpanSeconds={(historyEnd - historyStart).TotalSeconds:0}";
                }

                if (unresolved.Count == 0)
                {
                    return $"Callsign match diagnostics | unresolved=0 pilots={pilots.Count} {historyText}";
                }

                return $"Callsign match diagnostics | unresolvedShown={unresolved.Count} pilots={pilots.Count} {historyText} | " +
                    string.Join(" | ", unresolved);
            });
    }

    private sealed record VatsimDataFeed(
        VatsimGeneral? General,
        IReadOnlyList<VatsimPilot>? Pilots);

    private sealed record VatsimGeneral(
        [property: JsonPropertyName("update_timestamp")]
        DateTimeOffset? UpdateTimestamp);

    private sealed record VatsimPilot(
        string Callsign,
        double Latitude,
        double Longitude,
        int Altitude,
        int Groundspeed,
        int Heading,
        [property: JsonPropertyName("last_updated")]
        DateTimeOffset? LastUpdated,
        [property: JsonPropertyName("cid")]
        JsonElement? Cid);

    private sealed record CallsignConfirmation(
        string CandidateCallsign,
        int CandidateMatchCount,
        DateTimeOffset? LastPilotUpdateTime,
        DateTimeOffset? LastObservationTime,
        string? ConfirmedCallsign,
        bool Confirmed,
        DateTimeOffset? CandidateFirstObservationTime,
        string? PreviousConfirmedCallsign = null);
}

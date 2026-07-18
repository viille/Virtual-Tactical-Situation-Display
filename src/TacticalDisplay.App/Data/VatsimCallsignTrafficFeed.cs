using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using TacticalDisplay.App.Services;
using TacticalDisplay.Core.Models;
using TacticalDisplay.Core.Services;

namespace TacticalDisplay.App.Data;

public sealed class VatsimCallsignTrafficFeed : ITrafficDataFeed
{
    private const string LogSource = "VATSIM";
    private const int RequiredStableCallsignMatches = 2;
    private const int RequiredStableCallsignSwitchMatches = 3;
    private const double StrongSwitchMaxDistanceNm = 0.75;
    private const double StrongSwitchMaxAltitudeDeltaFt = 400;
    private static readonly TimeSpan SnapshotHistoryRetention = TimeSpan.FromSeconds(60);

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly ITrafficDataFeed _inner;
    private readonly TacticalDisplaySettings _settings;
    private readonly HttpClient _httpClient;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private readonly object _historyLock = new();
    private readonly Queue<TrafficSnapshot> _snapshotHistory = new();
    private readonly Dictionary<string, CallsignConfirmation> _callsignConfirmations = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyList<VatsimPilotCandidate> _cachedPilots = [];
    private bool _hasPilotFeedSnapshot;
    private DateTimeOffset _lastRefreshAt = DateTimeOffset.MinValue;
    private int _isEnriching;

    public VatsimCallsignTrafficFeed(ITrafficDataFeed inner, TacticalDisplaySettings settings)
    {
        _inner = inner;
        _settings = settings;
        _httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(6)
        };
        _httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("Tactical-Situation-Display");
        _inner.SnapshotReceived += OnInnerSnapshotReceived;
        _inner.ConnectionChanged += OnInnerConnectionChanged;
        DataSourceDebugLog.Info(LogSource, $"VATSIM callsign lookup enabled | feed={GetFeedUri()} refreshSeconds={Math.Clamp(_settings.VatsimCallsignRefreshSeconds, 15, 300):0}");
    }

    public event EventHandler<TrafficSnapshot>? SnapshotReceived;
    public event EventHandler<bool>? ConnectionChanged;
    public bool IsConnected => _inner.IsConnected;

    public Task StartAsync(CancellationToken cancellationToken) =>
        _inner.StartAsync(cancellationToken);

    public Task StopAsync() =>
        _inner.StopAsync();

    public async ValueTask DisposeAsync()
    {
        _inner.SnapshotReceived -= OnInnerSnapshotReceived;
        _inner.ConnectionChanged -= OnInnerConnectionChanged;
        await _inner.DisposeAsync();
        _refreshLock.Dispose();
        _httpClient.Dispose();
    }

    private void OnInnerConnectionChanged(object? sender, bool connected) =>
        ConnectionChanged?.Invoke(sender, connected);

    private void OnInnerSnapshotReceived(object? sender, TrafficSnapshot snapshot)
    {
        RememberSnapshot(snapshot);
        if (Interlocked.Exchange(ref _isEnriching, 1) == 1)
        {
            SnapshotReceived?.Invoke(this, snapshot);
            return;
        }

        _ = EnrichAndPublishAsync(snapshot);
    }

    private async Task EnrichAndPublishAsync(TrafficSnapshot snapshot)
    {
        try
        {
            var pilots = await GetPilotsAsync(CancellationToken.None).ConfigureAwait(false);
            var history = GetSnapshotHistory();
            LogCallsignMatchDiagnostics(snapshot, history, pilots);
            var enriched = VatsimCallsignMatcher.EnrichSnapshotFromHistory(snapshot, history, pilots);
            enriched = ConfirmCallsignMatches(snapshot, enriched, pilots);
            LogEnrichmentSummary(snapshot, enriched, pilots);
            SnapshotReceived?.Invoke(this, enriched);
        }
        catch (Exception ex)
        {
            DataSourceDebugLog.ThrottledDebug(
                LogSource,
                "callsign-enrichment-failed",
                TimeSpan.FromSeconds(30),
                () => $"Callsign lookup failed; publishing simulator snapshot unchanged | error={ex.Message}");
            SnapshotReceived?.Invoke(this, snapshot);
        }
        finally
        {
            Interlocked.Exchange(ref _isEnriching, 0);
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
                    pilot.LastUpdated ?? feed.General?.UpdateTimestamp))
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
        IReadOnlyList<VatsimPilotCandidate> pilots)
    {
        var activeContactIds = enriched.Contacts
            .Select(static contact => contact.Id)
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
            if (!string.IsNullOrWhiteSpace(originalContact.Callsign) ||
                string.IsNullOrWhiteSpace(enrichedContact.Callsign))
            {
                confirmedContacts.Add(enrichedContact);
                continue;
            }

            var callsign = enrichedContact.Callsign.Trim().ToUpperInvariant();
            pilotUpdatesByCallsign.TryGetValue(callsign, out var pilotUpdateTime);
            var pilot = pilots.FirstOrDefault(candidate =>
                string.Equals(candidate.Callsign, callsign, StringComparison.OrdinalIgnoreCase));
            var matchDiagnostics = pilot is null
                ? VatsimMatchDiagnostics.None
                : VatsimCallsignMatcher.InspectMatch(originalContact, pilot);
            var confirmation = UpdateCallsignConfirmation(
                enrichedContact.Id,
                callsign,
                pilotUpdateTime,
                originalContact.Timestamp,
                IsStrongSwitchMatch(matchDiagnostics));
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

        return enriched with { Contacts = confirmedContacts };
    }

    private CallsignConfirmation UpdateCallsignConfirmation(
        string contactId,
        string callsign,
        DateTimeOffset? pilotUpdateTime,
        DateTimeOffset observationTime,
        bool strongMatch)
    {
        if (!_callsignConfirmations.TryGetValue(contactId, out var confirmation))
        {
            confirmation = new CallsignConfirmation(callsign, 0, null, null, null, false);
        }
        else if (!string.Equals(confirmation.CandidateCallsign, callsign, StringComparison.OrdinalIgnoreCase))
        {
            confirmation = confirmation with
            {
                CandidateCallsign = callsign,
                CandidateMatchCount = 0
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

        var confirmedCallsign = confirmation.ConfirmedCallsign;
        // A very close direct match is safe enough to show immediately. This
        // improves visibility for short-lived or stationary contacts while
        // ordinary and historical matches still require two observations.
        if (confirmedCallsign is null && strongMatch)
        {
            confirmedCallsign = callsign;
        }
        else if (confirmedCallsign is null && count >= RequiredStableCallsignMatches)
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

    private Uri GetFeedUri()
    {
        if (Uri.TryCreate(_settings.VatsimDataFeedUrl, UriKind.Absolute, out var uri) &&
            uri.Scheme is "http" or "https")
        {
            return uri;
        }

        return new Uri("https://data.vatsim.net/v3/vatsim-data.json");
    }

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
                var nearest = original.Contacts.Count == 0
                    ? VatsimMatchDiagnostics.None
                    : original.Contacts
                        .Select(contact => VatsimCallsignMatcher.InspectBestMatch(contact, pilots))
                        .OrderBy(match => match.Score)
                        .FirstOrDefault() ?? VatsimMatchDiagnostics.None;

                return "Callsign enrichment summary | " +
                    $"contacts={original.Contacts.Count} pilots={pilots.Count} added={added} missing={missing} " +
                    $"nearest={nearest.Callsign ?? "n/a"} nearestDistanceNm={nearest.DistanceNm:0.00} " +
                    $"nearestAltitudeDeltaFt={nearest.AltitudeDeltaFt:0} nearestMatch={nearest.IsMatch} " +
                    $"reject={nearest.RejectReason ?? "none"}";
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
        DateTimeOffset? LastUpdated);

    private sealed record CallsignConfirmation(
        string CandidateCallsign,
        int CandidateMatchCount,
        DateTimeOffset? LastPilotUpdateTime,
        DateTimeOffset? LastObservationTime,
        string? ConfirmedCallsign,
        bool Confirmed);
}

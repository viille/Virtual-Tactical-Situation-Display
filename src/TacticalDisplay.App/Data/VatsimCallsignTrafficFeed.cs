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
    private static readonly TimeSpan SnapshotHistoryRetention = TimeSpan.FromSeconds(20);

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly ITrafficDataFeed _inner;
    private readonly TacticalDisplaySettings _settings;
    private readonly HttpClient _httpClient;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private readonly object _historyLock = new();
    private readonly Queue<TrafficSnapshot> _snapshotHistory = new();
    private readonly Dictionary<string, CallsignConfirmation> _callsignConfirmations = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyList<VatsimPilotCandidate> _cachedPilots = [];
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
        if (_cachedPilots.Count > 0 && now - _lastRefreshAt < refreshInterval)
        {
            return _cachedPilots;
        }

        await _refreshLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            now = DateTimeOffset.UtcNow;
            if (_cachedPilots.Count > 0 && now - _lastRefreshAt < refreshInterval)
            {
                return _cachedPilots;
            }

            using var response = await _httpClient.GetAsync(GetFeedUri(), cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            var feed = await JsonSerializer.DeserializeAsync<VatsimDataFeed>(stream, JsonOptions, cancellationToken).ConfigureAwait(false);
            _cachedPilots = feed?.Pilots?
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
            var confirmation = UpdateCallsignConfirmation(
                enrichedContact.Id,
                callsign,
                pilotUpdateTime);

            if (confirmation.Confirmed)
            {
                confirmedContacts.Add(enrichedContact with { Callsign = callsign });
                continue;
            }

            confirmedContacts.Add(enrichedContact with { Callsign = null });
        }

        return enriched with { Contacts = confirmedContacts };
    }

    private CallsignConfirmation UpdateCallsignConfirmation(
        string contactId,
        string callsign,
        DateTimeOffset? pilotUpdateTime)
    {
        if (!_callsignConfirmations.TryGetValue(contactId, out var confirmation) ||
            !string.Equals(confirmation.Callsign, callsign, StringComparison.OrdinalIgnoreCase))
        {
            confirmation = new CallsignConfirmation(callsign, 0, null, false);
        }

        var count = confirmation.MatchCount;
        if (!pilotUpdateTime.HasValue ||
            confirmation.LastPilotUpdateTime is null ||
            pilotUpdateTime.Value > confirmation.LastPilotUpdateTime.Value)
        {
            count++;
        }

        confirmation = confirmation with
        {
            MatchCount = count,
            LastPilotUpdateTime = pilotUpdateTime ?? confirmation.LastPilotUpdateTime,
            Confirmed = confirmation.Confirmed || count >= RequiredStableCallsignMatches
        };
        _callsignConfirmations[contactId] = confirmation;
        return confirmation;
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
        string Callsign,
        int MatchCount,
        DateTimeOffset? LastPilotUpdateTime,
        bool Confirmed);
}

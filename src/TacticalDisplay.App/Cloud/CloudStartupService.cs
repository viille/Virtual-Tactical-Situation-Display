using TacticalDisplay.App.Storage;

namespace TacticalDisplay.App.Cloud;

public sealed class CloudStartupService
{
    private readonly AuthService _auth; private readonly CollectionService _collections;
    private readonly CloudOverlaySettingsStore _overlays; private readonly CloudPreferencesStore _preferences;
    private readonly ICloudTelemetryService _telemetry; private readonly object _lock = new(); private Task? _initialization;
    private readonly CloudContentStore _content;
    public CloudStartupService(AuthService auth, CollectionService collections, CloudOverlaySettingsStore overlays,
        CloudPreferencesStore preferences, ICloudTelemetryService telemetry, CloudContentStore content)
    { _auth = auth; _collections = collections; _overlays = overlays; _preferences = preferences; _telemetry = telemetry; _content = content; }

    public Task InitializeAsync(CancellationToken ct)
    {
        lock (_lock) return _initialization ??= InitializeCoreAsync(ct);
    }
    public Task TrackClosedAsync(CancellationToken ct) => _telemetry.TrackAsync("app_closed", ct: ct);
    public CloudContentStore Content => _content;
    private async Task InitializeCoreAsync(CancellationToken ct)
    {
        await _content.LoadAuthorizedCacheAsync().ConfigureAwait(false);
        await _telemetry.TrackAsync("app_started", ct: ct).ConfigureAwait(false);
        await _auth.InitializeAsync(ct).ConfigureAwait(false);
        try
        {
            var remote = (await _collections.FetchAsync(ct).ConfigureAwait(false)).ToList();
            var cached = (await _collections.LoadCachedAsync().ConfigureAwait(false)).ToDictionary(x => x.Slug, StringComparer.OrdinalIgnoreCase);
            _overlays.Apply(remote);
            _content.ReconcileOnlineAuthorization(remote);
            if (!_preferences.Load().AutoSyncEnabled) return;
            foreach (var collection in remote.Where(x =>
                         (x.CacheOffline && (!cached.TryGetValue(x.Slug, out var local) || !string.Equals(local.CachedVersion, x.CurrentVersion, StringComparison.Ordinal))) ||
                         (!x.CacheOffline && (x.ShowKneepadPages || x.ShowMapFeaturesOnRadar) && !_content.HasContent(x.Slug))))
                await _collections.SyncAsync(collection, ct).ConfigureAwait(false);
        }
        catch (CloudApiException) { }
    }
}

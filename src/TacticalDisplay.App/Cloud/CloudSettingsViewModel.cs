using System.Collections.ObjectModel;
using System.ComponentModel;
using TacticalDisplay.App.ViewModels;

namespace TacticalDisplay.App.Cloud;

public sealed class CloudSettingsViewModel : ViewModelBase
{
    private readonly AuthService _auth; private readonly CollectionService _collections; private readonly Storage.CloudOverlaySettingsStore _overlaySettings;
    private readonly Storage.CloudPreferencesStore _preferencesStore; private readonly ICloudTelemetryService _telemetry;
    private readonly CloudStartupService _startup;
    private readonly CloudContentStore _content;
    private readonly SynchronizationContext? _uiContext;
    private string _statusText = "Loading cached collections..."; private string _cloudStatus = "Offline";
    private bool _isBusy; private bool _autoSyncEnabled; private Collection? _selectedCollection; private DateTimeOffset? _lastSync;
    public CloudSettingsViewModel(AuthService auth, CollectionService collections, Storage.CloudOverlaySettingsStore overlaySettings,
        Storage.CloudPreferencesStore preferencesStore, ICloudTelemetryService telemetry, CloudStartupService startup)
    {
        _auth = auth; _collections = collections; _overlaySettings = overlaySettings; _preferencesStore = preferencesStore; _telemetry = telemetry; _startup = startup;
        _content = startup.Content; _uiContext = SynchronizationContext.Current;
        var preferences = _preferencesStore.Load(); _autoSyncEnabled = preferences.AutoSyncEnabled;
        foreach (var type in Enum.GetValues<MapFeatureType>()) FeatureFilters.Add(new MapFeatureFilterViewModel(type, preferences.EnabledFeatureTypes.Contains(type)));
        _auth.StateChanged += (_, state) => { if (_uiContext is null) ApplyAuth(state); else _uiContext.Post(_ => ApplyAuth(state), null); };
    }
    public ObservableCollection<Collection> Collections { get; } = [];
    public ObservableCollection<MapFeatureFilterViewModel> FeatureFilters { get; } = [];
    public AuthState AuthState => _auth.State;
    public string AccountText => AuthState.Status switch
    {
        AuthStatus.SignedIn => $"Signed in with VATSIM\n{AuthState.User?.DisplayName} · CID {AuthState.User?.VatsimCid}",
        AuthStatus.SigningIn => "Waiting for browser login...", AuthStatus.SessionExpired => "Session expired. Sign in again.",
        AuthStatus.OfflineUnknown => "Account could not be verified while offline.", _ => "Not signed in"
    };
    public string StatusText { get => _statusText; private set => SetField(ref _statusText, value); }
    public string CloudStatus { get => _cloudStatus; private set => SetField(ref _cloudStatus, value); }
    public bool IsBusy { get => _isBusy; private set => SetField(ref _isBusy, value); }
    public bool IsSignedIn => AuthState.Status == AuthStatus.SignedIn;
    public bool CanSignIn => AuthState.Status is AuthStatus.SignedOut or AuthStatus.SessionExpired or AuthStatus.OfflineUnknown;
    public bool IsSigningIn => AuthState.Status == AuthStatus.SigningIn;
    public bool CanSignOut => AuthState.Status is AuthStatus.SignedIn or AuthStatus.OfflineUnknown;
    public bool AutoSyncEnabled { get => _autoSyncEnabled; set => SetField(ref _autoSyncEnabled, value); }
    public Collection? SelectedCollection
    {
        get => _selectedCollection;
        set
        {
            if (_selectedCollection is not null) _selectedCollection.PropertyChanged -= OnSelectedCollectionChanged;
            SetField(ref _selectedCollection, value);
            if (_selectedCollection is not null) _selectedCollection.PropertyChanged += OnSelectedCollectionChanged;
            Raise(nameof(CanOpenKneepad));
        }
    }
    public bool CanOpenKneepad => SelectedCollection is { ShowKneepadPages: true } selected && _content.GetPages(selected.Slug).Count > 0;
    public string LastSyncText => _lastSync is null ? "Never synced" : $"Last sync: {_lastSync.Value.ToLocalTime():g}";

    public async Task InitializeAsync(CancellationToken ct)
    {
        Replace(await _collections.LoadCachedAsync().ConfigureAwait(true));
        try { await _startup.InitializeAsync(ct).ConfigureAwait(true); await RefreshAsync(ct).ConfigureAwait(true); }
        catch (CloudApiException ex)
        {
            CloudStatus = ex.IsNetworkError ? "Offline" : "Error";
            StatusText = ex.IsNetworkError ? "Showing authorized cached collections." : ex.Message;
        }
        catch (Exception ex) { CloudStatus = "Error"; StatusText = ex.Message; }
    }
    public async Task SignInAsync(Action<Uri> openBrowser, CancellationToken ct) => await RunAsync(async () => { await _auth.SignInAsync(openBrowser, ct); await RefreshCoreAsync(ct); }, "Signed in.");
    public async Task SignOutAsync(CancellationToken ct) => await RunAsync(async () => { await _auth.SignOutAsync(ct); await RefreshCoreAsync(ct); }, "Signed out. Authorized cached content remains available offline.");
    public Task RefreshAsync(CancellationToken ct) => RunAsync(() => RefreshCoreAsync(ct), "Collections refreshed.");
    public async Task SyncAsync(CancellationToken ct)
    {
        await RunAsync(async () =>
        {
            var remote = (await _collections.FetchAsync(ct)).ToList(); _overlaySettings.Apply(remote);
            _content.ReconcileOnlineAuthorization(remote);
            foreach (var collection in remote) await _collections.SyncAsync(collection, ct);
            MergeCachedMetadata(remote, await _collections.LoadCachedAsync()); Replace(remote);
        }, "Accessible collections synced.");
    }
    public async Task<Collection?> RedeemAsync(string code, CancellationToken ct)
    {
        if (!IsSignedIn) { StatusText = "Sign in with VATSIM first."; return null; }
        if (IsBusy) return null; IsBusy = true;
        try
        {
            var knownSlugs = Collections.Select(x => x.Slug).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var response = await _collections.RedeemAsync(code, ct); await RefreshCoreAsync(ct);
            StatusText = "Share code redeemed. The collection is now available under Shared with me."; CloudStatus = "Online";
            if (!string.IsNullOrWhiteSpace(response.Collection?.Slug))
                return Collections.FirstOrDefault(x => string.Equals(x.Slug, response.Collection.Slug, StringComparison.OrdinalIgnoreCase)) ?? response.Collection;
            return Collections.FirstOrDefault(x => !knownSlugs.Contains(x.Slug) && x.AccessSource == CollectionAccessSource.Shared);
        }
        catch (OperationCanceledException) { StatusText = "Operation cancelled."; return null; }
        catch (CloudApiException ex) { CloudStatus = ex.IsNetworkError ? "Offline" : "Error"; StatusText = FriendlyMessage(ex); return null; }
        finally { IsBusy = false; }
    }
    public Task SyncCollectionAsync(Collection collection, CancellationToken ct) =>
        RunAsync(async () => { await _collections.SyncAsync(collection, ct); await RefreshCoreAsync(ct); }, "Collection synced.");
    public async Task SaveSettingsAsync()
    {
        _overlaySettings.Save(Collections);
        _preferencesStore.Save(new Storage.CloudPreferences { AutoSyncEnabled = AutoSyncEnabled, EnabledFeatureTypes = FeatureFilters.Where(x => x.IsEnabled).Select(x => x.Type).ToHashSet() });
        foreach (var collection in Collections.Where(x => !x.CacheOffline)) await _collections.RemoveCachedAsync(collection.Slug);
        if (Collections.Any(x => x.ShowMapFeaturesOnRadar)) await _telemetry.TrackAsync("map_feature_overlay_enabled");
    }
    public Task TrackKneepadOpenedAsync(CancellationToken ct) => _telemetry.TrackAsync("kneepad_page_opened", ct: ct);
    private async Task RefreshCoreAsync(CancellationToken ct)
    {
        var remote = (await _collections.FetchAsync(ct)).ToList();
        var cachedItems = await _collections.LoadCachedAsync();
        var cached = cachedItems.ToDictionary(x => x.Slug, StringComparer.OrdinalIgnoreCase);
        MergeCachedMetadata(remote, cachedItems);
        _overlaySettings.Apply(remote);
        _content.ReconcileOnlineAuthorization(remote);
        if (AutoSyncEnabled)
        {
            foreach (var collection in remote.Where(x =>
                         (x.CacheOffline && (x.CachedVersion is null || x.UpdateAvailable)) ||
                         (!x.CacheOffline && (x.ShowKneepadPages || x.ShowMapFeaturesOnRadar) && !_content.HasContent(x.Slug))))
                await _collections.SyncAsync(collection, ct);
            cached = (await _collections.LoadCachedAsync()).ToDictionary(x => x.Slug, StringComparer.OrdinalIgnoreCase);
            foreach (var collection in remote.Where(x => cached.ContainsKey(x.Slug)))
            {
                collection.CachedVersion = cached[collection.Slug].CachedVersion; collection.LastSyncedAt = cached[collection.Slug].LastSyncedAt;
            }
        }
        Replace(remote); CloudStatus = "Online";
    }
    private async Task RunAsync(Func<Task> action, string success)
    {
        if (IsBusy) return; IsBusy = true;
        try { await action(); StatusText = success; CloudStatus = "Online"; }
        catch (OperationCanceledException) { StatusText = "Operation cancelled."; }
        catch (CloudApiException ex) { CloudStatus = ex.IsNetworkError ? "Offline" : "Error"; StatusText = FriendlyMessage(ex); }
        finally { IsBusy = false; }
    }
    private static string FriendlyMessage(CloudApiException ex) => ex.ErrorCode switch
    {
        "SHARE_CODE_INVALID" => "Invalid or expired share code.", "UNAUTHORIZED" => "Sign in with VATSIM first.",
        "FORBIDDEN" => "You do not have permission to redeem this code.", "RATE_LIMITED" => "Too many attempts. Try again later.", _ => ex.Message
    };
    private void ApplyAuth(AuthState state)
    {
        Raise(nameof(AuthState)); Raise(nameof(AccountText)); Raise(nameof(IsSignedIn)); Raise(nameof(CanSignIn)); Raise(nameof(IsSigningIn)); Raise(nameof(CanSignOut));
    }
    private void OnSelectedCollectionChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(Collection.ShowKneepadPages)) Raise(nameof(CanOpenKneepad));
    }
    private static void MergeCachedMetadata(IEnumerable<Collection> remote, IEnumerable<Collection> cachedItems)
    {
        var cached = cachedItems.ToDictionary(x => x.Slug, StringComparer.OrdinalIgnoreCase);
        foreach (var collection in remote)
        {
            if (!cached.TryGetValue(collection.Slug, out var local)) { collection.CachedVersion = null; collection.LastSyncedAt = null; continue; }
            collection.CachedVersion = local.CachedVersion; collection.LastSyncedAt = local.LastSyncedAt;
        }
    }
    private void Replace(IEnumerable<Collection> items)
    {
        var materialized = items.OrderBy(x => x.AccessSource).ThenBy(x => x.Name).ToList(); _overlaySettings.Apply(materialized);
        Collections.Clear(); foreach (var item in materialized) Collections.Add(item);
        _lastSync = Collections.MaxBy(x => x.LastSyncedAt)?.LastSyncedAt; Raise(nameof(LastSyncText));
    }
}

public sealed class MapFeatureFilterViewModel : ViewModelBase
{
    private bool _isEnabled;
    public MapFeatureFilterViewModel(MapFeatureType type, bool enabled) { Type = type; _isEnabled = enabled; }
    public MapFeatureType Type { get; }
    public string Name => Type.ToString();
    public bool IsEnabled { get => _isEnabled; set => SetField(ref _isEnabled, value); }
}

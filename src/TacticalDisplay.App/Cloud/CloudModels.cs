using System.Text.Json;
using System.Text.Json.Serialization;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace TacticalDisplay.App.Cloud;

public enum CollectionVisibility { Public, Private, Unlisted }

public enum CollectionAccessSource { Public, Owner, Shared, Organization }

public sealed class Collection : INotifyPropertyChanged
{
    private bool _showKneepadPages = true; private bool _showMapFeaturesOnRadar; private bool _cacheOffline = true; private bool _isActive;
    public string Slug { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public CollectionVisibility Visibility { get; set; }
    public CollectionAccessSource AccessSource { get; set; }
    public string CurrentVersion { get; set; } = "";
    public bool Unlocked { get; set; }
    public string? OwnerName { get; set; }
    public string? OrganizationName { get; set; }
    public DateTimeOffset? LastSyncedAt { get; set; }
    public string? CachedVersion { get; set; }
    [JsonIgnore] public bool ShowKneepadPages { get => _showKneepadPages; set => SetField(ref _showKneepadPages, value); }
    [JsonIgnore] public bool ShowMapFeaturesOnRadar { get => _showMapFeaturesOnRadar; set => SetField(ref _showMapFeaturesOnRadar, value); }
    [JsonIgnore] public bool CacheOffline { get => _cacheOffline; set => SetField(ref _cacheOffline, value); }
    [JsonIgnore] public bool IsActive { get => _isActive; set => SetField(ref _isActive, value); }
    [JsonIgnore] public bool UpdateAvailable => !string.Equals(CurrentVersion, CachedVersion, StringComparison.Ordinal);
    [JsonIgnore] public string GroupName => AccessSource switch
    {
        CollectionAccessSource.Owner => "My Collections",
        CollectionAccessSource.Shared => "Shared with me",
        CollectionAccessSource.Organization => "Organizations",
        _ => "Public"
    };
    public event PropertyChangedEventHandler? PropertyChanged;
    private void SetField(ref bool field, bool value, [CallerMemberName] string? name = null)
    {
        if (field == value) return; field = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}

public sealed class UserAccount
{
    public string Id { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string? Email { get; set; }
    public string VatsimCid { get; set; } = "";
}

public sealed class KneepadPage
{
    public string CollectionSlug { get; set; } = "";
    public string Slug { get; set; } = "";
    public string Title { get; set; } = "";
    public string? Category { get; set; }
    public List<string> Tags { get; set; } = [];
    public string Version { get; set; } = "";
    public int OrderIndex { get; set; }
    public string ContentMarkdown { get; set; } = "";
}

public enum MapFeatureGeometryType { Point, Line, Polygon, Orbit }

public enum MapFeatureType { Airbase, Bullseye, Waypoint, Target, Threat, Sam, Tanker, Awacs, Cap, Ip, Egress, Divert, Reference, RestrictedArea, Custom }

public sealed class MapFeature
{
    public string CollectionSlug { get; set; } = "";
    public string Slug { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Label { get; set; }
    public string? DisplayName { get; set; }
    public MapFeatureType FeatureType { get; set; }
    public MapFeatureGeometryType GeometryType { get; set; }
    public JsonElement Geometry { get; set; }
    public int? AltitudeM { get; set; }
    public string? Description { get; set; }
    public string? Icon { get; set; }
    public string? Color { get; set; }
    public bool DisplayLabel { get; set; }
    public bool VisibleOnRadar { get; set; }
    public bool VisibleOnKneepadMap { get; set; }
    public int OrderIndex { get; set; }
    [JsonIgnore]
    public string TacticalLabel =>
        !string.IsNullOrWhiteSpace(Label) ? Label :
        !string.IsNullOrWhiteSpace(DisplayName) ? DisplayName :
        Name;
}

public sealed class DeviceLoginStartResponse
{
    public string RequestId { get; set; } = "";
    public string RequestToken { get; set; } = "";
    public string LoginUrl { get; set; } = "";
    public DateTimeOffset ExpiresAt { get; set; }
    public int? PollIntervalSeconds { get; set; }
    public int? IntervalSeconds { get; set; }
    public int? PollInterval { get; set; }
}

public sealed class DeviceLoginStatusResponse
{
    public string Status { get; set; } = "";
    public string? SessionToken { get; set; }
    public UserAccount? User { get; set; }
    public string? Message { get; set; }
}

public sealed class MeResponse { public UserAccount User { get; set; } = new(); }
public sealed class CollectionsResponse { public List<Collection> Collections { get; set; } = []; }
public sealed class RedeemShareCodeResponse { public Collection? Collection { get; set; } }
public sealed class CollectionPagesResponse { public List<KneepadPage> Pages { get; set; } = []; }
public sealed class CollectionMapFeaturesResponse { public List<MapFeature> MapFeatures { get; set; } = []; }
public sealed class TelemetryEventRequest { public string EventName { get; set; } = ""; public object? Payload { get; set; } }

public enum AuthStatus { SignedOut, SigningIn, SignedIn, SessionExpired, OfflineUnknown }
public sealed record AuthState(AuthStatus Status, UserAccount? User = null);

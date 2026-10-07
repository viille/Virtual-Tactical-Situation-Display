using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;
using Microsoft.Win32;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using TacticalDisplay.App.Commands;
using TacticalDisplay.App.Cloud;
using TacticalDisplay.App.Data;
using TacticalDisplay.App.Services;
using TacticalDisplay.App.TacticalLink;
using TacticalDisplay.Core.Config;
using TacticalDisplay.Core.Math;
using TacticalDisplay.Core.Models;
using TacticalDisplay.Core.Services;
using KneepadPage = TacticalDisplay.Core.Models.KneepadPage;

namespace TacticalDisplay.App.ViewModels;

public sealed class MainViewModel : ViewModelBase, IAsyncDisposable
{
    private static readonly TimeSpan CommandRefreshThreshold = TimeSpan.FromMilliseconds(1500);
    private static readonly TimeSpan CommandRefreshThrottle = TimeSpan.FromSeconds(1);
    private readonly JsonConfigStore _configStore;
    private readonly ClassificationConfig _classification;
    private readonly AirspaceDataService _airspaceDataService = new();
    private TrafficRepository _repository = new();
    private ITrafficDataFeed _feed;
    private readonly TacticalLinkClient _tacticalLink;
    private readonly AarClient _aarClient;
    private readonly AarFuelTransferCoordinator _aarFuelTransferCoordinator;
    public AarViewModel Aar { get; }
    private readonly AuthService _auth;
    private bool _showTacticalLinkMenu;
    private string _tacticalLinkMessage = "TacticalLink is disconnected.";
    private string _msfsAarBridgeStatusText = "Checking MSFS 2024 package folder…";
    private readonly Msfs2024PackagePathResolver _msfsPackagePathResolver = new();
    private readonly MsfsAarBridgeInstaller _msfsAarBridgeInstaller = new();
    private readonly DispatcherTimer _tacticalTelemetryTimer;
    private OwnshipState? _latestOwnship;
    private readonly DispatcherTimer _renderTimer;
    private readonly DispatcherTimer _airspaceTimer;
    private readonly CancellationTokenSource _runCts = new();
    private TacticalPicture? _picture;
    private IReadOnlyList<AirspaceArea> _airspaces = [];
    private string _connectionText = "Disconnected";
    private string _simConnectText = "N/A";
    private string _trafficText = "0 contacts";
    private string _sourceText = string.Empty;
    private string _webDisplayText = "Web: starting";
    private string _airspaceText = "Airspace: loading";
    private string _bullseyeLatitudeText = string.Empty;
    private string _bullseyeLongitudeText = string.Empty;
    private string _bullseyeText = "Bullseye: off";
    private string _xPlane12ApiBaseUrlText = string.Empty;
    private string _minTrackedAltitudeFtText = string.Empty;
    private bool _simConnected;
    private int _refreshCounter;
    private DateTimeOffset _rateWindowStart = DateTimeOffset.UtcNow;
    private string _refreshRateText = "0.0 Hz";
    private string _selectedDataSource = DataSourceModes.Demo;
    private bool _showSettings;
    private bool _isAlwaysOnTop;
    private bool _showKneepad;
    private bool _showBullseyePopup;
    private bool _interceptSelectionArmed;
    private string? _interceptTargetId;
    private bool _hasActiveCloudCollections;
    private Uri? _cloudDashboardUri;
    private CloudKneepadPageViewModel? _selectedCloudKneepadPage;
    private readonly Dictionary<string, ManualTargetMetadata> _manualTargetMetadata;
    private bool _showSimConnectDebugOnFailure;
    private DateTimeOffset? _simConnectCheckingStartedAt;
    private DateTimeOffset _lastCommandRefreshAt = DateTimeOffset.MinValue;

    public MainViewModel()
    {
        AppDataPaths.MigrateLegacyConfigIfNeeded();
        var configPath = ResolveConfigDirectory();
        DataSourceDebugLog.Info("App", $"Application startup | configDir={configPath} logFile={DataSourceDebugLog.CurrentLogFilePath}");
        _configStore = new JsonConfigStore(configPath);
        Settings = _configStore.LoadDisplaySettings();
        Settings.AarFuelUnit = string.Equals(Settings.AarFuelUnit, "LB", StringComparison.OrdinalIgnoreCase) ? "LB" : "KG";
        Settings.Msfs2024CommunityFolder = _msfsPackagePathResolver.ResolveCommunity2024(Settings.Msfs2024CommunityFolder) ?? Settings.Msfs2024CommunityFolder;
        RefreshMsfsAarBridgeStatus();
        DataSourceDebugLog.SetEnabled(Settings.EnableDataSourceDebugLogging);
        DataSourceDebugLog.Info("App", $"Debug logging enabled={Settings.EnableDataSourceDebugLogging}");
        _classification = _configStore.LoadClassification();
        _manualTargetMetadata = _configStore.LoadManualTargetMetadata();
        Settings.DataSourceMode = DataSourceModes.Normalize(Settings.DataSourceMode);
        _auth = CloudBootstrapper.Provider.GetRequiredService<AuthService>();
        _auth.StateChanged += OnCloudAuthStateChanged;
        _tacticalLink = new TacticalLinkClient(CloudBootstrapper.Provider.GetRequiredService<VtsdCloudClient>());
        _aarClient = new AarClient(_tacticalLink, _runCts.Token);
        _tacticalLink.StateChanged += OnTacticalLinkStateChanged;
        _feed = TrafficFeedFactory.Create(Settings, () => _tacticalLink.NearbyPeers);
        _aarFuelTransferCoordinator = new AarFuelTransferCoordinator(_aarClient, () => _feed as IAarFuelAdapter,
            () => Aar?.ProtectedReserveKg ?? 0, _runCts.Token);
        Aar = new AarViewModel(_aarClient, _aarFuelTransferCoordinator, _tacticalLink, Settings,
            () => _feed as IAarFuelAdapter, () => TacticalLinkPeers, () => _tacticalLink.LocalCapabilities.Contains("aar.tanker"),
            () => _tacticalLink.LocalCapabilities.Contains("aar.receiver"), () => IsTacticalLinkConnected,
            UpdateAarSampling, _runCts.Token);
        Aar.PropertyChanged += OnAarViewModelPropertyChanged;
        _feed.ConnectionChanged += OnConnectionChanged;
        _feed.SnapshotReceived += OnSnapshotReceived;
        if (_feed is IAarPoseSource initialPoseSource) initialPoseSource.AarPoseSampled += OnAarPoseSampled;
        if (_feed is IAarFuelAdapter initialFuelAdapter) initialFuelAdapter.FuelSampled += OnAarFuelSampled;
        if (_feed is IAarBridgeRuntimeStatusSource initialBridgeStatus)
        {
            initialBridgeStatus.BridgeRuntimeStateChanged += OnAarBridgeRuntimeStateChanged;
            RefreshMsfsAarBridgeStatus();
        }

        ToggleOrientationCommand = CreateUiCommand(nameof(ToggleOrientationCommand), ToggleOrientation);
        IncreaseRangeCommand = CreateUiCommand(nameof(IncreaseRangeCommand), IncreaseRange);
        DecreaseRangeCommand = CreateUiCommand(nameof(DecreaseRangeCommand), DecreaseRange);
        IncreaseMapOpacityCommand = CreateUiCommand(nameof(IncreaseMapOpacityCommand), () => AdjustMapOpacity(0.05));
        DecreaseMapOpacityCommand = CreateUiCommand(nameof(DecreaseMapOpacityCommand), () => AdjustMapOpacity(-0.05));
        IncreaseMapOverlayOpacityCommand = CreateUiCommand(nameof(IncreaseMapOverlayOpacityCommand), () => AdjustMapOverlayOpacity(0.05));
        DecreaseMapOverlayOpacityCommand = CreateUiCommand(nameof(DecreaseMapOverlayOpacityCommand), () => AdjustMapOverlayOpacity(-0.05));
        IncreaseTargetSymbolScaleCommand = CreateUiCommand(nameof(IncreaseTargetSymbolScaleCommand), () => AdjustTargetSymbolScale(0.1));
        DecreaseTargetSymbolScaleCommand = CreateUiCommand(nameof(DecreaseTargetSymbolScaleCommand), () => AdjustTargetSymbolScale(-0.1));
        ToggleDeclutterCommand = CreateUiCommand(nameof(ToggleDeclutterCommand), ToggleDeclutter);
        ToggleMapCommand = CreateUiCommand(nameof(ToggleMapCommand), ToggleMap);
        ToggleAirspaceCommand = CreateUiCommand(nameof(ToggleAirspaceCommand), ToggleAirspace);
        ToggleControlledAirspaceCommand = CreateUiCommand(nameof(ToggleControlledAirspaceCommand), ToggleControlledAirspace);
        ToggleLabelsCommand = CreateUiCommand(nameof(ToggleLabelsCommand), ToggleLabels);
        ToggleTrailsCommand = CreateUiCommand(nameof(ToggleTrailsCommand), ToggleTrails);
        ToggleBullseyeCommand = CreateUiCommand(nameof(ToggleBullseyeCommand), ToggleBullseye);
        ToggleInterceptCommand = CreateUiCommand(nameof(ToggleInterceptCommand), ToggleIntercept);
        ApplyBullseyeCommand = CreateUiCommand(nameof(ApplyBullseyeCommand), ApplyBullseye);
        ClearBullseyeCommand = CreateUiCommand(nameof(ClearBullseyeCommand), ClearBullseye);
        CancelBullseyeCommand = CreateUiCommand(nameof(CancelBullseyeCommand), CancelBullseye);
        SaveSettingsCommand = CreateUiCommand(nameof(SaveSettingsCommand), () => _configStore.SaveDisplaySettings(Settings));
        ApplyDataSourceCommand = CreateUiCommand(nameof(ApplyDataSourceCommand), ApplyDataSource);
        ToggleSettingsCommand = CreateUiCommand(nameof(ToggleSettingsCommand), ToggleSettingsPanel);
        ToggleTacticalLinkMenuCommand = CreateUiCommand(nameof(ToggleTacticalLinkMenuCommand), ToggleTacticalLinkMenu);
        ConnectTacticalLinkCommand = CreateUiCommand(nameof(ConnectTacticalLinkCommand), () => _ = ConnectTacticalLinkAsync());
        DisconnectTacticalLinkCommand = CreateUiCommand(nameof(DisconnectTacticalLinkCommand), () => _ = DisconnectTacticalLinkAsync());
        SelectMsfs2024CommunityFolderCommand = CreateUiCommand(nameof(SelectMsfs2024CommunityFolderCommand), SelectMsfs2024CommunityFolder);
        InstallMsfsAarBridgeCommand = CreateUiCommand(nameof(InstallMsfsAarBridgeCommand), () => _ = InstallMsfsAarBridgeAsync());
        UninstallMsfsAarBridgeCommand = CreateUiCommand(nameof(UninstallMsfsAarBridgeCommand), UninstallMsfsAarBridge);
        ToggleAlwaysOnTopCommand = CreateUiCommand(nameof(ToggleAlwaysOnTopCommand), ToggleAlwaysOnTop);
        ToggleKneepadCommand = CreateUiCommand(nameof(ToggleKneepadCommand), ToggleKneepad);
        PreviousKneepadPageCommand = CreateUiCommand(nameof(PreviousKneepadPageCommand), PreviousKneepadPage);
        NextKneepadPageCommand = CreateUiCommand(nameof(NextKneepadPageCommand), NextKneepadPage);
        PreviousCloudKneepadPageCommand = CreateUiCommand(nameof(PreviousCloudKneepadPageCommand), PreviousCloudKneepadPage);
        NextCloudKneepadPageCommand = CreateUiCommand(nameof(NextCloudKneepadPageCommand), NextCloudKneepadPage);
        AddKneepadPageCommand = CreateUiCommand(nameof(AddKneepadPageCommand), AddKneepadPage);
        DeleteKneepadPageCommand = CreateUiCommand(nameof(DeleteKneepadPageCommand), DeleteKneepadPage);
        SetKneepadMissionPageCommand = CreateUiCommand(nameof(SetKneepadMissionPageCommand), SetKneepadMissionPage);
        SetKneepadImagePageCommand = CreateUiCommand(nameof(SetKneepadImagePageCommand), SetKneepadImagePage);
        SetKneepadUrlPageCommand = CreateUiCommand(nameof(SetKneepadUrlPageCommand), SetKneepadUrlPage);
        ImportKneepadImageCommand = CreateUiCommand(nameof(ImportKneepadImageCommand), ImportKneepadImage);
        ClearKneepadImageCommand = CreateUiCommand(nameof(ClearKneepadImageCommand), ClearKneepadImage);
        OpenDebugLogFolderCommand = CreateUiCommand(nameof(OpenDebugLogFolderCommand), OpenDebugLogFolder);
        ClearWebViewCookiesCommand = CreateUiCommand(nameof(ClearWebViewCookiesCommand), ClearWebViewCookies);
        ResetHotkeysCommand = CreateUiCommand(nameof(ResetHotkeysCommand), ResetHotkeys);
        HotkeyRows = new ObservableCollection<HotkeyBindingViewModel>(
            HotkeyDefaults.Actions.Select(action => new HotkeyBindingViewModel(
                action.DisplayName,
                EnsureHotkeyBinding(action.Action),
                NotifyHotkeyBindingsChanged)));
        AirspaceRegionOptions = new ObservableCollection<AirspaceRegionOptionViewModel>(
            CreateAirspaceRegionOptions());

        _renderTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1000.0 / Settings.RenderRateFps) };
        _renderTimer.Tick += (_, _) =>
        {
            RefreshPicture();
            UpdateSimConnectCheckingTimeout();
        };
        _renderTimer.Start();
        _tacticalTelemetryTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1000.0 / Settings.TacticalLinkTelemetryRateHz) };
        _tacticalTelemetryTimer.Tick += (_, _) => PublishLatestTacticalTelemetry();
        _tacticalTelemetryTimer.Start();
        _ = _feed.StartAsync(_runCts.Token);
        _airspaceTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(5) };
        _airspaceTimer.Tick += (_, _) => _ = LoadAirspacesAsync();
        _airspaceTimer.Start();
        _ = LoadAirspacesAsync();
        InitializeBullseyeText();
        XPlane12ApiBaseUrlText = Settings.XPlane12ApiBaseUrl;
        MinTrackedAltitudeFtText = Settings.MinTrackedAltitudeFt.ToString("0.##", CultureInfo.InvariantCulture);

        UpdateSourceState(Settings.DataSourceMode);
    }

    public TacticalDisplaySettings Settings { get; }
    public bool SuppressModalDialogs { get; set; }
    public event EventHandler? HotkeyBindingsChanged;

    public TacticalPicture? Picture
    {
        get => _picture;
        private set => SetField(ref _picture, value);
    }

    public IReadOnlyList<AirspaceArea> Airspaces
    {
        get => _airspaces;
        private set => SetField(ref _airspaces, value);
    }

    public string ConnectionText
    {
        get => _connectionText;
        private set => SetField(ref _connectionText, value);
    }

    public string TrafficText
    {
        get => _trafficText;
        private set => SetField(ref _trafficText, value);
    }

    public string SimConnectText
    {
        get => _simConnectText;
        private set
        {
            SetField(ref _simConnectText, value);
            Raise(nameof(SimulatorFooterText));
        }
    }

    public string SourceText
    {
        get => _sourceText;
        private set => SetField(ref _sourceText, value);
    }

    public string WebDisplayText
    {
        get => _webDisplayText;
        private set => SetField(ref _webDisplayText, value);
    }

    public string AirspaceText
    {
        get => _airspaceText;
        private set => SetField(ref _airspaceText, value);
    }

    public double AirspaceOpacityPercent
    {
        get => Math.Round(Settings.AirspaceOpacity * 100);
        set
        {
            var opacity = Math.Clamp(value / 100.0, 0.1, 1.0);
            if (Math.Abs(Settings.AirspaceOpacity - opacity) < 0.001)
            {
                return;
            }

            Settings.AirspaceOpacity = opacity;
            Raise();
            Raise(nameof(AirspaceOpacityText));
            Raise(nameof(Settings));
        }
    }

    public string AirspaceOpacityText => $"Area opacity: {AirspaceOpacityPercent:0}%";

    public ObservableCollection<AirspaceRegionOptionViewModel> AirspaceRegionOptions { get; }

    public double MapOpacityPercent
    {
        get => Math.Round(Settings.MapOpacity * 100);
        set
        {
            var opacity = Math.Clamp(value / 100.0, 0.0, 1.0);
            if (Math.Abs(Settings.MapOpacity - opacity) < 0.001)
            {
                return;
            }

            Settings.MapOpacity = opacity;
            Raise();
            Raise(nameof(MapOpacityText));
            Raise(nameof(Settings));
        }
    }

    public string MapOpacityText => $"Map opacity: {MapOpacityPercent:0}%";

    public double MapLabelBackgroundOpacityPercent
    {
        get => Math.Round(Settings.MapLabelBackgroundOpacity * 100);
        set
        {
            var opacity = Math.Clamp(value / 100.0, 0.0, 1.0);
            if (Math.Abs(Settings.MapLabelBackgroundOpacity - opacity) < 0.001)
            {
                return;
            }

            Settings.MapLabelBackgroundOpacity = opacity;
            Raise();
            Raise(nameof(MapLabelBackgroundOpacityText));
            Raise(nameof(Settings));
        }
    }

    public string MapLabelBackgroundOpacityText => $"Label background: {MapLabelBackgroundOpacityPercent:0}%";

    public string BullseyeLatitudeText
    {
        get => _bullseyeLatitudeText;
        set => SetField(ref _bullseyeLatitudeText, value);
    }

    public string BullseyeLongitudeText
    {
        get => _bullseyeLongitudeText;
        set => SetField(ref _bullseyeLongitudeText, value);
    }

    public string BullseyeText
    {
        get => _bullseyeText;
        private set => SetField(ref _bullseyeText, value);
    }

    public string XPlane12ApiBaseUrlText
    {
        get => _xPlane12ApiBaseUrlText;
        set => SetField(ref _xPlane12ApiBaseUrlText, value);
    }

    public string MinTrackedAltitudeFtText
    {
        get => _minTrackedAltitudeFtText;
        set
        {
            SetField(ref _minTrackedAltitudeFtText, value);

            var normalized = value.Trim().Replace(',', '.');
            if (!double.TryParse(normalized, NumberStyles.Float, CultureInfo.InvariantCulture, out var altitudeFt) ||
                altitudeFt < 0)
            {
                return;
            }

            Settings.MinTrackedAltitudeFt = altitudeFt;
            Raise(nameof(Settings));
        }
    }

    public string RefreshRateText
    {
        get => _refreshRateText;
        private set => SetField(ref _refreshRateText, value);
    }

    public bool SimConnected
    {
        get => _simConnected;
        private set => SetField(ref _simConnected, value);
    }

    public string SelectedDataSource
    {
        get => _selectedDataSource;
        set
        {
            SetField(ref _selectedDataSource, value);
            Raise(nameof(ShowMsfsSettings));
            Raise(nameof(ShowXPlane12Settings));
            Raise(nameof(ShowXPlaneLegacySettings));
        }
    }

    public bool DataSourceDebugLoggingEnabled
    {
        get => Settings.EnableDataSourceDebugLogging;
        set
        {
            if (Settings.EnableDataSourceDebugLogging == value)
            {
                return;
            }

            Settings.EnableDataSourceDebugLogging = value;
            DataSourceDebugLog.SetEnabled(value);
            DataSourceDebugLog.Info("App", $"Debug logging toggled | enabled={value}");
            Raise();
        }
    }

    public bool DiagnosticTelemetryEnabled
    {
        get => Settings.EnableDiagnosticTelemetry;
        set
        {
            if (Settings.EnableDiagnosticTelemetry == value)
            {
                return;
            }

            Settings.EnableDiagnosticTelemetry = value;
            _configStore.SaveDisplaySettings(Settings);
            DataSourceDebugLog.Info("App", $"Diagnostic telemetry toggled | enabled={value}");
            Raise();
        }
    }

    public void SetDiagnosticTelemetryConsent(bool enabled)
    {
        Settings.EnableDiagnosticTelemetry = enabled;
        Settings.DiagnosticTelemetryConsentAsked = true;
        _configStore.SaveDisplaySettings(Settings);
        DataSourceDebugLog.Info("App", $"Diagnostic telemetry consent answered | enabled={enabled}");
        Raise(nameof(DiagnosticTelemetryEnabled));
    }

    public bool WebServerEnabled
    {
        get => Settings.EnableWebServer;
        set
        {
            if (Settings.EnableWebServer == value)
            {
                return;
            }

            Settings.EnableWebServer = value;
            _configStore.SaveDisplaySettings(Settings);
            Raise();
        }
    }

    public bool VatsimCallsignLookupEnabled
    {
        get => Settings.EnableVatsimCallsignLookup;
        set
        {
            if (Settings.EnableVatsimCallsignLookup == value)
            {
                return;
            }

            Settings.EnableVatsimCallsignLookup = value;
            _configStore.SaveDisplaySettings(Settings);
            DataSourceDebugLog.Info("VATSIM", $"VATSIM callsign lookup toggled | enabled={value}");
            Raise();

            if (DataSourceModes.UsesSimulatorConnection(Settings.DataSourceMode))
            {
                _ = SwitchDataSourceAsync(Settings.DataSourceMode, forceRestart: true);
            }
        }
    }

    public string OwnCallsign
    {
        get => Settings.OwnCallsign;
        set
        {
            var normalized = new string((value ?? string.Empty).Trim().ToUpperInvariant()
                .Where(character => char.IsLetterOrDigit(character) || character is '-' or ' ')
                .Take(32)
                .ToArray());
            if (string.Equals(Settings.OwnCallsign, normalized, StringComparison.Ordinal)) return;
            Settings.OwnCallsign = normalized;
            _configStore.SaveDisplaySettings(Settings);
            Raise();
        }
    }

    public bool TacticalLinkAutoReconnect
    {
        get => Settings.TacticalLinkAutoReconnect;
        set
        {
            if (Settings.TacticalLinkAutoReconnect == value) return;
            Settings.TacticalLinkAutoReconnect = value;
            _tacticalLink.SetAutoReconnect(value);
            _configStore.SaveDisplaySettings(Settings);
            Raise();
        }
    }

    public int TacticalLinkTelemetryRateHz
    {
        get => Settings.TacticalLinkTelemetryRateHz;
        set
        {
            var normalized = value >= 20 ? 20 : 10;
            if (Settings.TacticalLinkTelemetryRateHz == normalized) return;
            Settings.TacticalLinkTelemetryRateHz = normalized;
            _tacticalTelemetryTimer.Interval = TimeSpan.FromMilliseconds(1000.0 / normalized);
            _configStore.SaveDisplaySettings(Settings);
            Raise();
        }
    }

    public IReadOnlyList<int> TacticalLinkTelemetryRateOptions { get; } = [10, 20];

    public double TacticalLinkInterestRadiusNm
    {
        get => Settings.TacticalLinkInterestRadiusNm;
        set
        {
            if (!double.IsFinite(value)) return;
            var normalized = Math.Clamp(value, 10, 500);
            if (Math.Abs(Settings.TacticalLinkInterestRadiusNm - normalized) < 0.001) return;
            Settings.TacticalLinkInterestRadiusNm = normalized;
            _configStore.SaveDisplaySettings(Settings);
            _ = _tacticalLink.SetInterestRadiusAsync(normalized, _runCts.Token);
            Raise();
        }
    }

    public bool TacticalLinkDebugDiagnostics
    {
        get => Settings.TacticalLinkDebugDiagnostics;
        set { if (Settings.TacticalLinkDebugDiagnostics == value) return; Settings.TacticalLinkDebugDiagnostics = value; _configStore.SaveDisplaySettings(Settings); Raise(); }
    }

    public bool ShowSettings
    {
        get => _showSettings;
        private set
        {
            SetField(ref _showSettings, value);
            Raise(nameof(SettingsToggleText));
        }
    }

    public bool ShowTacticalLinkMenu
    {
        get => _showTacticalLinkMenu;
        private set => SetField(ref _showTacticalLinkMenu, value);
    }
    public bool ShowUtilityPanel => ShowSettings || ShowTacticalLinkMenu;
    public string TacticalLinkStatusText => _tacticalLink.ConnectionState switch
    {
        TacticalLinkConnectionState.Connected => "TL CONNECTED",
        TacticalLinkConnectionState.Connecting => "TL CONNECTING",
        TacticalLinkConnectionState.Degraded => "TL RECONNECTING",
        _ => "TL OFF"
    };
    public string TacticalLinkIdentityText => _tacticalLink.LocalCallsign is { Length: > 0 } callsign
        ? $"VATSIM identity: {callsign}"
        : _auth.State.User is { } user
            ? $"VATSIM CID: {user.VatsimCid}"
            : "VATSIM sign-in required.";
    public string TacticalLinkAircraftTypeText => $"Aircraft: {_tacticalLink.LocalAircraftType ?? "UNKNOWN"}";
    public bool IsCloudSignedIn => _auth.State.Status == AuthStatus.SignedIn;
    public TacticalLinkState TacticalLinkState => _tacticalLink.Snapshot();
    public string TacticalLinkMessage { get => _tacticalLinkMessage; private set => SetField(ref _tacticalLinkMessage, value); }
    public string TacticalLinkPeerCountText => $"Nearby TacticalLink traffic: {_tacticalLink.NearbyPeers.Count}";
    public IReadOnlyList<TacticalLinkPeerDisplay> TacticalLinkPeers => _tacticalLink.NearbyPeers.Select(peer =>
    {
        var range = _repository.Ownship is { } ownship && peer.LatestTelemetry is { } state
            ? GeoMath.DistanceNm(ownship.LatitudeDeg, ownship.LongitudeDeg, state.LatitudeDeg, state.LongitudeDeg)
            : (double?)null;
        return new TacticalLinkPeerDisplay(peer.ParticipantId, peer.Callsign, peer.AircraftType ?? "Aircraft", range is { } nm ? $"{nm:0} NM" : "--- NM", peer.StatusText);
    }).ToArray();
    public string MsfsAarBridgeStatusText { get => _msfsAarBridgeStatusText; private set => SetField(ref _msfsAarBridgeStatusText, value); }
    public bool IsTacticalLinkConnected => _tacticalLink.ConnectionState == TacticalLinkConnectionState.Connected;

    public bool IsAlwaysOnTop
    {
        get => _isAlwaysOnTop;
        private set
        {
            SetField(ref _isAlwaysOnTop, value);
            Raise(nameof(TopMostToggleText));
        }
    }

    public bool ShowBullseyePopup
    {
        get => _showBullseyePopup;
        private set => SetField(ref _showBullseyePopup, value);
    }

    public bool ShowKneepad
    {
        get => _showKneepad;
        private set
        {
            SetField(ref _showKneepad, value);
            Raise(nameof(KneepadToggleText));
            Raise(nameof(ShowCloudKneepad));
            Raise(nameof(ShowLocalKneepad));
            RaiseKneepadState();
        }
    }

    public bool BullseyeButtonActive =>
        Settings.ShowBullseye &&
        Settings.BullseyeLatitudeDeg.HasValue &&
        Settings.BullseyeLongitudeDeg.HasValue;

    public string SettingsToggleText => ShowSettings ? "Hide Settings" : "Show Settings";
    public string TopMostToggleText => IsAlwaysOnTop ? "Unpin Window" : "Pin On Top";
    public string KneepadToggleText => ShowKneepad ? "KNEE ON" : "KNEE";
    public string AppVersionText { get; set; } = "ver unknown";
    public bool InterceptSelectionArmed
    {
        get => _interceptSelectionArmed;
        private set
        {
            SetField(ref _interceptSelectionArmed, value);
            Raise(nameof(InterceptToggleText));
            Raise(nameof(InterceptModeActive));
        }
    }

    public string? InterceptTargetId
    {
        get => _interceptTargetId;
        private set
        {
            SetField(ref _interceptTargetId, value);
            Raise(nameof(InterceptToggleText));
            Raise(nameof(InterceptModeActive));
        }
    }

    public bool InterceptModeActive => InterceptSelectionArmed || !string.IsNullOrWhiteSpace(InterceptTargetId);
    public string InterceptToggleText => InterceptSelectionArmed
        ? "INT SEL"
        : string.IsNullOrWhiteSpace(InterceptTargetId) ? "INT" : "INT ON";
    public bool ShowMsfsSettings => DataSourceModes.IsMsfs(SelectedDataSource);
    public bool ShowXPlane12Settings => DataSourceModes.IsXPlane12(SelectedDataSource);
    public string XPlane12AarStatusText
    {
        get
        {
            var connected = _feed?.IsConnected == true;
            var pose = _feed is IAarPoseSource ? "Ready (10 Hz during operations)" : "Unavailable";
            var fuel = _feed as IAarFuelAdapter;
            var read = connected && fuel?.CanReadFuel == true ? "Ready" : "Unavailable";
            var write = connected && fuel?.CanWriteFuel == true ? "Ready" : "Unavailable";
            return $"Web API {(connected ? "Connected" : "Disconnected")} · Pose {pose} · Fuel read {read} · write {write}";
        }
    }
    public bool ShowXPlaneLegacySettings => DataSourceModes.IsXPlaneLegacy(SelectedDataSource);
    public string SimulatorStatusLabel =>
        DataSourceModes.IsXPlane12(Settings.DataSourceMode) ? "X-Plane 12:" :
        DataSourceModes.IsXPlaneLegacy(Settings.DataSourceMode) ? "XPUIPC:" :
        "Simulator:";
    public string SimulatorFooterText => $"{SimulatorStatusLabel} {SimConnectText}";

    public IReadOnlyDictionary<string, ManualTargetMetadata> ManualTargetMetadata => _manualTargetMetadata;
    public ObservableCollection<string> AvailableDataSources { get; } =
    [
        DataSourceModes.Demo,
        DataSourceModes.Msfs,
        DataSourceModes.XPlane12,
        DataSourceModes.XPlaneLegacy
    ];
    public ObservableCollection<string> AvailableDirectionReferences { get; } =
    [
        "TRUE",
        "MAG"
    ];
    public ObservableCollection<string> AvailableKneepadContentModes { get; } =
    [
        "Mission",
        "Image",
        "Url"
    ];
    public ObservableCollection<CloudKneepadPageViewModel> CloudKneepadPages { get; } = [];

    public CloudKneepadPageViewModel? SelectedCloudKneepadPage
    {
        get => _selectedCloudKneepadPage;
        set
        {
            SetField(ref _selectedCloudKneepadPage, value);
            Raise(nameof(CloudKneepadTitle));
            Raise(nameof(CloudKneepadContent));
            Raise(nameof(CloudKneepadPageText));
            Raise(nameof(CloudDashboardUri));
        }
    }

    public bool HasActiveCloudKneepad => _hasActiveCloudCollections;
    public bool ShowCloudKneepad => ShowKneepad && HasActiveCloudKneepad;
    public bool ShowLocalKneepad => ShowKneepad && !HasActiveCloudKneepad;
    public string CloudKneepadTitle => SelectedCloudKneepadPage is { } page
        ? $"{page.CollectionName} - {page.Title}"
        : "Cloud Kneepad";
    public string CloudKneepadContent => SelectedCloudKneepadPage?.ContentMarkdown ?? "No synced kneepad pages for the active collections.";
    public string CloudKneepadPageText => SelectedCloudKneepadPage is { } page
        ? $"Cloud {CloudKneepadPages.IndexOf(page) + 1}/{CloudKneepadPages.Count}"
        : "Cloud 0/0";
    public Uri? CloudDashboardUri => SelectedCloudKneepadPage?.DashboardUri ?? _cloudDashboardUri;

    public string SelectedKneepadContentMode
    {
        get => CurrentKneepadPage.ContentMode;
        set
        {
            var mode = NormalizeKneepadContentMode(value);
            if (string.Equals(CurrentKneepadPage.ContentMode, mode, StringComparison.Ordinal))
            {
                return;
            }

            CurrentKneepadPage.ContentMode = mode;
            SyncLegacyKneepadSettings();
            Raise();
            RaiseKneepadState();
        }
    }

    public string KneepadMissionInformationText
    {
        get => CurrentKneepadPage.MissionInformation;
        set
        {
            if (CurrentKneepadPage.MissionInformation == value)
            {
                return;
            }

            CurrentKneepadPage.MissionInformation = value;
            SyncLegacyKneepadSettings();
            Raise();
            RaiseKneepadState();
        }
    }

    public string KneepadUrlText
    {
        get => CurrentKneepadPage.Url;
        set
        {
            if (CurrentKneepadPage.Url == value)
            {
                return;
            }

            CurrentKneepadPage.Url = value.Trim();
            SyncLegacyKneepadSettings();
            Raise();
            RaiseKneepadState();
        }
    }

    public string KneepadImagePathText => string.IsNullOrWhiteSpace(CurrentKneepadPage.ImagePath)
        ? "No image selected"
        : CurrentKneepadPage.ImagePath;

    public Uri? KneepadUrl
    {
        get
        {
            var url = NormalizeKneepadUrl(CurrentKneepadPage.Url);
            return Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri : null;
        }
    }

    public bool ShowKneepadMission => ShowKneepad &&
        string.Equals(SelectedKneepadContentMode, "Mission", StringComparison.Ordinal);

    public bool ShowKneepadImage => ShowKneepad &&
        string.Equals(SelectedKneepadContentMode, "Image", StringComparison.Ordinal) &&
        !string.IsNullOrWhiteSpace(CurrentKneepadPage.ImagePath) &&
        File.Exists(CurrentKneepadPage.ImagePath);

    public bool ShowKneepadUrl => ShowKneepad &&
        string.Equals(SelectedKneepadContentMode, "Url", StringComparison.Ordinal) &&
        KneepadUrl is not null;

    public bool ShowKneepadPageChooser => ShowKneepad &&
        string.Equals(SelectedKneepadContentMode, "Empty", StringComparison.Ordinal);

    public bool ShowKneepadPlaceholder => ShowKneepad &&
        !ShowKneepadPageChooser &&
        !ShowKneepadMission &&
        !string.Equals(SelectedKneepadContentMode, "Image", StringComparison.Ordinal) &&
        !string.Equals(SelectedKneepadContentMode, "Url", StringComparison.Ordinal);

    public string KneepadPlaceholderText =>
        SelectedKneepadContentMode switch
        {
            "Image" => "No kneepad image selected",
            "Url" => "No valid kneepad URL",
            _ => "No mission information"
        };

    public string KneepadPageText => $"Page {Settings.SelectedKneepadPageIndex + 1}/{Settings.KneepadPages.Count}";
    public string KneepadImagePath => CurrentKneepadPage.ImagePath;

    public string SelectedDirectionReference
    {
        get => DirectionReferenceLabel;
        set
        {
            var mode = string.Equals(value, "MAG", StringComparison.OrdinalIgnoreCase)
                ? DirectionReferenceMode.Magnetic
                : DirectionReferenceMode.True;
            if (Settings.DirectionReferenceMode == mode)
            {
                return;
            }

            Settings.DirectionReferenceMode = mode;
            Raise();
            Raise(nameof(HeaderText));
            Raise(nameof(Settings));
        }
    }

    public RelayCommand ToggleOrientationCommand { get; }
    public RelayCommand IncreaseRangeCommand { get; }
    public RelayCommand DecreaseRangeCommand { get; }
    public RelayCommand IncreaseMapOpacityCommand { get; }
    public RelayCommand DecreaseMapOpacityCommand { get; }
    public RelayCommand IncreaseMapOverlayOpacityCommand { get; }
    public RelayCommand DecreaseMapOverlayOpacityCommand { get; }
    public RelayCommand IncreaseTargetSymbolScaleCommand { get; }
    public RelayCommand DecreaseTargetSymbolScaleCommand { get; }
    public RelayCommand ToggleDeclutterCommand { get; }
    public RelayCommand ToggleMapCommand { get; }
    public RelayCommand ToggleAirspaceCommand { get; }
    public RelayCommand ToggleControlledAirspaceCommand { get; }
    public RelayCommand ToggleLabelsCommand { get; }
    public RelayCommand ToggleTrailsCommand { get; }
    public RelayCommand ToggleBullseyeCommand { get; }
    public RelayCommand ToggleInterceptCommand { get; }
    public RelayCommand ApplyBullseyeCommand { get; }
    public RelayCommand ClearBullseyeCommand { get; }
    public RelayCommand CancelBullseyeCommand { get; }
    public RelayCommand SaveSettingsCommand { get; }
    public RelayCommand ApplyDataSourceCommand { get; }
    public RelayCommand ToggleSettingsCommand { get; }
    public RelayCommand ToggleTacticalLinkMenuCommand { get; }
    public RelayCommand ConnectTacticalLinkCommand { get; }
    public RelayCommand DisconnectTacticalLinkCommand { get; }
    public RelayCommand SelectMsfs2024CommunityFolderCommand { get; }
    public RelayCommand InstallMsfsAarBridgeCommand { get; }
    public RelayCommand UninstallMsfsAarBridgeCommand { get; }
    public RelayCommand ToggleAlwaysOnTopCommand { get; }
    public RelayCommand ToggleKneepadCommand { get; }
    public RelayCommand PreviousKneepadPageCommand { get; }
    public RelayCommand NextKneepadPageCommand { get; }
    public RelayCommand PreviousCloudKneepadPageCommand { get; }
    public RelayCommand NextCloudKneepadPageCommand { get; }
    public RelayCommand AddKneepadPageCommand { get; }
    public RelayCommand DeleteKneepadPageCommand { get; }
    public RelayCommand SetKneepadMissionPageCommand { get; }
    public RelayCommand SetKneepadImagePageCommand { get; }
    public RelayCommand SetKneepadUrlPageCommand { get; }
    public RelayCommand ImportKneepadImageCommand { get; }
    public RelayCommand ClearKneepadImageCommand { get; }
    public RelayCommand OpenDebugLogFolderCommand { get; }
    public RelayCommand ClearWebViewCookiesCommand { get; }
    public RelayCommand ResetHotkeysCommand { get; }
    public ObservableCollection<HotkeyBindingViewModel> HotkeyRows { get; }

    public string HeaderText =>
        $"RANGE {Settings.SelectedRangeNm} NM";

    private string DirectionReferenceLabel => Settings.DirectionReferenceMode == DirectionReferenceMode.Magnetic ? "MAG" : "TRUE";

    public void RequestCommandRefresh(string reason, TimeSpan elapsed)
    {
        if (!Application.Current.Dispatcher.CheckAccess())
        {
            Application.Current.Dispatcher.BeginInvoke(() => RequestCommandRefresh(reason, elapsed));
            return;
        }

        var now = DateTimeOffset.UtcNow;
        if (now - _lastCommandRefreshAt < CommandRefreshThrottle)
        {
            return;
        }

        _lastCommandRefreshAt = now;
        DataSourceDebugLog.Warn("App", $"Command response was slow; refreshing display state | reason={reason} elapsedMs={elapsed.TotalMilliseconds:0}");
        RefreshPicture();
        RaiseCommandStateProperties();
    }

    public async ValueTask DisposeAsync()
    {
        DataSourceDebugLog.Info("App", "Application shutdown");
        try
        {
            _renderTimer.Stop();
            _tacticalTelemetryTimer.Stop();
            _airspaceTimer.Stop();
            _runCts.Cancel();
            Aar.Dispose();
            _aarFuelTransferCoordinator.Dispose();
            _aarClient.Dispose();
            await _tacticalLink.DisposeAsync();
            await _feed.StopAsync();
            if (_feed is IAarBridgeRuntimeStatusSource bridgeStatus) bridgeStatus.BridgeRuntimeStateChanged -= OnAarBridgeRuntimeStateChanged;
            await _feed.DisposeAsync();
        }
        finally
        {
            _configStore.SaveDisplaySettings(Settings);
            _configStore.SaveManualTargetMetadata(_manualTargetMetadata);
            _airspaceDataService.Dispose();
            _runCts.Dispose();
        }
    }

    public bool ExecuteHotkeyAction(string action)
    {
        switch (action.Trim().ToLowerInvariant())
        {
            case "range-up":
                IncreaseRangeCommand.Execute(null);
                return true;
            case "range-down":
                DecreaseRangeCommand.Execute(null);
                return true;
            case "orientation":
                ToggleOrientationCommand.Execute(null);
                return true;
            case "map":
                ToggleMapCommand.Execute(null);
                return true;
            case "declutter":
                ToggleDeclutterCommand.Execute(null);
                return true;
            case "trails":
                ToggleTrailsCommand.Execute(null);
                return true;
            case "bullseye":
                ToggleBullseyeCommand.Execute(null);
                return true;
            case "intercept":
                ToggleInterceptCommand.Execute(null);
                return true;
            case "labels":
                ToggleLabelsCommand.Execute(null);
                return true;
            case "airspace":
            case "lara":
                ToggleAirspaceCommand.Execute(null);
                return true;
            case "area":
                ToggleControlledAirspaceCommand.Execute(null);
                return true;
            case "pin":
                ToggleAlwaysOnTopCommand.Execute(null);
                return true;
            case "fullscreen":
                // Fullscreen is owned by MainWindow because it changes native window bounds.
                return false;
            case "settings":
                ToggleSettingsCommand.Execute(null);
                return true;
            case "kneepad":
                ToggleKneepadCommand.Execute(null);
                return true;
            case "kneepad-prev":
                PreviousKneepadPageCommand.Execute(null);
                return true;
            case "kneepad-next":
                NextKneepadPageCommand.Execute(null);
                return true;
            default:
                DataSourceDebugLog.Warn("Input", $"Unknown hotkey action | action={action}");
                return false;
        }
    }

    private HotkeyBinding EnsureHotkeyBinding(string action)
    {
        var binding = Settings.Hotkeys.FirstOrDefault(binding =>
            string.Equals(binding.Action, action, StringComparison.OrdinalIgnoreCase));
        if (binding is not null)
        {
            return binding;
        }

        var defaultBinding = HotkeyDefaults.CreateDefaultBindings().First(binding =>
            string.Equals(binding.Action, action, StringComparison.OrdinalIgnoreCase));
        Settings.Hotkeys.Add(defaultBinding);
        return defaultBinding;
    }

    private void ResetHotkeys()
    {
        Settings.Hotkeys = HotkeyDefaults.CreateDefaultBindings();
        HotkeyRows.Clear();
        foreach (var action in HotkeyDefaults.Actions)
        {
            HotkeyRows.Add(new HotkeyBindingViewModel(
                action.DisplayName,
                EnsureHotkeyBinding(action.Action),
                NotifyHotkeyBindingsChanged));
        }

        NotifyHotkeyBindingsChanged();
    }

    private void NotifyHotkeyBindingsChanged()
    {
        _configStore.SaveDisplaySettings(Settings);
        HotkeyBindingsChanged?.Invoke(this, EventArgs.Empty);
    }

    private void ToggleOrientation()
    {
        Settings.OrientationMode = Settings.OrientationMode == ScopeOrientationMode.HeadingUp
            ? ScopeOrientationMode.NorthUp
            : ScopeOrientationMode.HeadingUp;
        Raise(nameof(HeaderText));
        Raise(nameof(Settings));
    }

    private void IncreaseRange()
    {
        var current = Array.IndexOf(Settings.RangeScaleOptionsNm, Settings.SelectedRangeNm);
        if (current >= 0 && current < Settings.RangeScaleOptionsNm.Length - 1)
        {
            Settings.SelectedRangeNm = Settings.RangeScaleOptionsNm[current + 1];
        }
        Raise(nameof(HeaderText));
        Raise(nameof(Settings));
    }

    private void DecreaseRange()
    {
        var current = Array.IndexOf(Settings.RangeScaleOptionsNm, Settings.SelectedRangeNm);
        if (current > 0)
        {
            Settings.SelectedRangeNm = Settings.RangeScaleOptionsNm[current - 1];
        }
        Raise(nameof(HeaderText));
        Raise(nameof(Settings));
    }

    private void AdjustMapOpacity(double delta)
    {
        MapOpacityPercent = Math.Round((Math.Clamp(Settings.MapOpacity + delta, 0.0, 1.0)) * 100);
    }

    private void AdjustMapOverlayOpacity(double delta)
    {
        MapLabelBackgroundOpacityPercent = Math.Round((Math.Clamp(Settings.MapLabelBackgroundOpacity + delta, 0.0, 1.0)) * 100);
    }

    private void AdjustTargetSymbolScale(double delta)
    {
        var scale = Math.Clamp(Settings.TargetSymbolScale + delta, 0.6, 1.8);
        if (Math.Abs(Settings.TargetSymbolScale - scale) < 0.001)
        {
            return;
        }

        Settings.TargetSymbolScale = scale;
        Raise(nameof(Settings));
    }

    private void ToggleDeclutter()
    {
        Settings.Declutter = !Settings.Declutter;
        Raise(nameof(Settings));
    }

    private void ToggleMap()
    {
        Settings.ShowMapLayer = !Settings.ShowMapLayer;
        Raise(nameof(Settings));
    }

    private void ToggleAirspace()
    {
        Settings.ShowAirspaceBoundaries = !Settings.ShowAirspaceBoundaries;
        Raise(nameof(Settings));
    }

    private RelayCommand CreateUiCommand(string name, Action execute) =>
        new(() =>
        {
            var startedAt = DateTimeOffset.UtcNow;
            execute();
            var elapsed = DateTimeOffset.UtcNow - startedAt;
            if (elapsed > CommandRefreshThreshold)
            {
                RequestCommandRefresh(name, elapsed);
            }
        });

    private void RaiseCommandStateProperties()
    {
        Raise(nameof(HeaderText));
        Raise(nameof(Settings));
        Raise(nameof(SimulatorFooterText));
        Raise(nameof(SettingsToggleText));
        Raise(nameof(TopMostToggleText));
        RaiseKneepadState();
    }

    private void ToggleControlledAirspace()
    {
        Settings.ShowControlledAirspaceLayer = !Settings.ShowControlledAirspaceLayer;
        Raise(nameof(Settings));
    }

    private void ToggleLabels()
    {
        Settings.LabelMode = Settings.LabelMode switch
        {
            LabelMode.Minimal => LabelMode.Full,
            LabelMode.Full => LabelMode.Off,
            _ => LabelMode.Minimal
        };
    }

    private void ToggleTrails()
    {
        Settings.TrailsEnabled = !Settings.TrailsEnabled;
        Raise(nameof(Settings));
    }

    private void ToggleBullseye()
    {
        if (Settings.ShowBullseye &&
            Settings.BullseyeLatitudeDeg.HasValue &&
            Settings.BullseyeLongitudeDeg.HasValue)
        {
            ClearBullseye();
            return;
        }

        if (Settings.BullseyeLatitudeDeg.HasValue && Settings.BullseyeLongitudeDeg.HasValue)
        {
            BullseyeLatitudeText = Settings.BullseyeLatitudeDeg.Value.ToString("0.######", CultureInfo.InvariantCulture);
            BullseyeLongitudeText = Settings.BullseyeLongitudeDeg.Value.ToString("0.######", CultureInfo.InvariantCulture);
        }

        ShowBullseyePopup = true;
        Raise(nameof(BullseyeButtonActive));
    }

    private void ToggleIntercept()
    {
        if (!InterceptSelectionArmed && !string.IsNullOrWhiteSpace(InterceptTargetId))
        {
            InterceptTargetId = null;
            RefreshPicture();
            return;
        }

        InterceptSelectionArmed = !InterceptSelectionArmed;
    }

    private void ToggleSettingsPanel()
    {
        ShowTacticalLinkMenu = false;
        ShowSettings = !ShowSettings;
        Raise(nameof(ShowUtilityPanel));
    }

    private void ToggleAlwaysOnTop() => IsAlwaysOnTop = !IsAlwaysOnTop;

    private void ToggleKneepad() => ShowKneepad = !ShowKneepad;

    private void PreviousKneepadPage()
    {
        if (HasActiveCloudKneepad)
        {
            PreviousCloudKneepadPage();
            return;
        }

        EnsureKneepadPage();
        if (Settings.KneepadPages.Count <= 1)
        {
            return;
        }

        Settings.SelectedKneepadPageIndex = Settings.SelectedKneepadPageIndex <= 0
            ? Settings.KneepadPages.Count - 1
            : Settings.SelectedKneepadPageIndex - 1;
        SyncLegacyKneepadSettings();
        RaiseKneepadState();
    }

    private void NextKneepadPage()
    {
        if (HasActiveCloudKneepad)
        {
            NextCloudKneepadPage();
            return;
        }

        EnsureKneepadPage();
        if (Settings.KneepadPages.Count <= 1)
        {
            return;
        }

        Settings.SelectedKneepadPageIndex = (Settings.SelectedKneepadPageIndex + 1) % Settings.KneepadPages.Count;
        SyncLegacyKneepadSettings();
        RaiseKneepadState();
    }

    private void PreviousCloudKneepadPage()
    {
        if (CloudKneepadPages.Count <= 1)
        {
            return;
        }

        var index = SelectedCloudKneepadPage is null ? 0 : CloudKneepadPages.IndexOf(SelectedCloudKneepadPage);
        SelectedCloudKneepadPage = CloudKneepadPages[index <= 0 ? CloudKneepadPages.Count - 1 : index - 1];
    }

    private void NextCloudKneepadPage()
    {
        if (CloudKneepadPages.Count <= 1)
        {
            return;
        }

        var index = SelectedCloudKneepadPage is null ? -1 : CloudKneepadPages.IndexOf(SelectedCloudKneepadPage);
        SelectedCloudKneepadPage = CloudKneepadPages[(index + 1) % CloudKneepadPages.Count];
    }

    public void SetCloudKneepadPages(IEnumerable<CloudKneepadPageViewModel> pages, bool hasActiveCloudCollections, Uri dashboardUri)
    {
        var selectedKey = SelectedCloudKneepadPage?.Key;
        var materialized = pages.ToList();
        _hasActiveCloudCollections = hasActiveCloudCollections;
        _cloudDashboardUri = dashboardUri;
        CloudKneepadPages.Clear();
        foreach (var page in materialized) CloudKneepadPages.Add(page);
        SelectedCloudKneepadPage = CloudKneepadPages.FirstOrDefault(x => x.Key == selectedKey) ?? CloudKneepadPages.FirstOrDefault();
        Raise(nameof(HasActiveCloudKneepad));
        Raise(nameof(ShowCloudKneepad));
        Raise(nameof(ShowLocalKneepad));
        RaiseKneepadState();
    }

    private void AddKneepadPage()
    {
        Settings.KneepadPages.Add(new KneepadPage());
        Settings.SelectedKneepadPageIndex = Settings.KneepadPages.Count - 1;
        SyncLegacyKneepadSettings();
        RaiseKneepadState();
    }

    private void DeleteKneepadPage()
    {
        EnsureKneepadPage();
        if (Settings.KneepadPages.Count == 1)
        {
            Settings.KneepadPages[0] = new KneepadPage();
            Settings.SelectedKneepadPageIndex = 0;
        }
        else
        {
            Settings.KneepadPages.RemoveAt(Settings.SelectedKneepadPageIndex);
            Settings.SelectedKneepadPageIndex = Math.Clamp(Settings.SelectedKneepadPageIndex, 0, Settings.KneepadPages.Count - 1);
        }

        SyncLegacyKneepadSettings();
        RaiseKneepadState();
    }

    private void SetKneepadMissionPage()
    {
        CurrentKneepadPage.ContentMode = "Mission";
        SyncLegacyKneepadSettings();
        Raise(nameof(SelectedKneepadContentMode));
        RaiseKneepadState();
    }

    private void SetKneepadImagePage()
    {
        CurrentKneepadPage.ContentMode = "Image";
        SyncLegacyKneepadSettings();
        Raise(nameof(SelectedKneepadContentMode));
        RaiseKneepadState();
    }

    private void SetKneepadUrlPage()
    {
        CurrentKneepadPage.ContentMode = "Url";
        SyncLegacyKneepadSettings();
        Raise(nameof(SelectedKneepadContentMode));
        RaiseKneepadState();
    }

    private void ImportKneepadImage()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Select kneepad image",
            Filter = "Image files|*.png;*.jpg;*.jpeg;*.bmp;*.gif|All files|*.*",
            CheckFileExists = true,
            Multiselect = false
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        CurrentKneepadPage.ImagePath = dialog.FileName;
        CurrentKneepadPage.ContentMode = "Image";
        SyncLegacyKneepadSettings();
        _configStore.SaveDisplaySettings(Settings);
        Raise(nameof(SelectedKneepadContentMode));
        Raise(nameof(KneepadImagePathText));
        RaiseKneepadState();
    }

    private void ClearKneepadImage()
    {
        CurrentKneepadPage.ImagePath = string.Empty;
        SyncLegacyKneepadSettings();
        _configStore.SaveDisplaySettings(Settings);
        Raise(nameof(KneepadImagePathText));
        RaiseKneepadState();
    }

    private void RaiseKneepadState()
    {
        Raise(nameof(KneepadUrl));
        Raise(nameof(KneepadPageText));
        Raise(nameof(KneepadImagePath));
        Raise(nameof(SelectedKneepadContentMode));
        Raise(nameof(KneepadMissionInformationText));
        Raise(nameof(KneepadUrlText));
        Raise(nameof(KneepadImagePathText));
        Raise(nameof(ShowKneepadMission));
        Raise(nameof(ShowKneepadImage));
        Raise(nameof(ShowKneepadUrl));
        Raise(nameof(ShowKneepadPageChooser));
        Raise(nameof(ShowKneepadPlaceholder));
        Raise(nameof(KneepadPlaceholderText));
        Raise(nameof(ShowCloudKneepad));
        Raise(nameof(ShowLocalKneepad));
    }

    private static string NormalizeKneepadContentMode(string? mode) =>
        string.Equals(mode, "Image", StringComparison.OrdinalIgnoreCase) ? "Image" :
        string.Equals(mode, "Url", StringComparison.OrdinalIgnoreCase) ? "Url" :
        string.Equals(mode, "Empty", StringComparison.OrdinalIgnoreCase) ? "Empty" :
        "Mission";

    private KneepadPage CurrentKneepadPage
    {
        get
        {
            EnsureKneepadPage();
            Settings.SelectedKneepadPageIndex = Math.Clamp(Settings.SelectedKneepadPageIndex, 0, Settings.KneepadPages.Count - 1);
            return Settings.KneepadPages[Settings.SelectedKneepadPageIndex];
        }
    }

    private void EnsureKneepadPage()
    {
        if (Settings.KneepadPages.Count == 0)
        {
            Settings.KneepadPages.Add(new KneepadPage());
            Settings.SelectedKneepadPageIndex = 0;
        }
    }

    private void SyncLegacyKneepadSettings()
    {
        var page = CurrentKneepadPage;
        Settings.KneepadContentMode = page.ContentMode;
        Settings.KneepadMissionInformation = page.MissionInformation;
        Settings.KneepadImagePath = page.ImagePath;
        Settings.KneepadUrl = page.Url;
    }

    private static string NormalizeKneepadUrl(string? value)
    {
        var trimmed = value?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return string.Empty;
        }

        return trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                ? trimmed
                : $"https://{trimmed}";
    }

    private void ApplyDataSource()
    {
        Settings.XPlane12ApiBaseUrl = NormalizeXPlane12ApiBaseUrl(XPlane12ApiBaseUrlText);
        XPlane12ApiBaseUrlText = Settings.XPlane12ApiBaseUrl;
        _configStore.SaveDisplaySettings(Settings);
        _ = SwitchDataSourceAsync(SelectedDataSource, forceRestart: true);
    }

    private static string NormalizeXPlane12ApiBaseUrl(string value)
    {
        var trimmed = value.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return "http://localhost:8086/";
        }

        if (!trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = $"http://{trimmed}";
        }

        return trimmed.EndsWith("/", StringComparison.Ordinal) ? trimmed : $"{trimmed}/";
    }

    private void InitializeBullseyeText()
    {
        if (!Settings.BullseyeLatitudeDeg.HasValue || !Settings.BullseyeLongitudeDeg.HasValue)
        {
            BullseyeLatitudeText = string.Empty;
            BullseyeLongitudeText = string.Empty;
            UpdateBullseyeText();
            return;
        }

        BullseyeLatitudeText = Settings.BullseyeLatitudeDeg.Value.ToString("0.######", CultureInfo.InvariantCulture);
        BullseyeLongitudeText = Settings.BullseyeLongitudeDeg.Value.ToString("0.######", CultureInfo.InvariantCulture);
        UpdateBullseyeText();
    }

    private void ApplyBullseye()
    {
        if (ApplyBullseyeCoordinates(BullseyeLatitudeText, BullseyeLongitudeText))
        {
            ShowBullseyePopup = false;
        }
    }

    public bool TryApplyBullseye(string latitudeText, string longitudeText)
    {
        BullseyeLatitudeText = latitudeText;
        BullseyeLongitudeText = longitudeText;
        return ApplyBullseyeCoordinates(latitudeText, longitudeText);
    }

    private bool ApplyBullseyeCoordinates(string latitudeText, string longitudeText)
    {
        if (!TryParseCoordinate(latitudeText, -90, 90, "NS", out var latitude) ||
            !TryParseCoordinate(longitudeText, -180, 180, "EW", out var longitude))
        {
            BullseyeText = "Bullseye: invalid coordinate";
            return false;
        }

        Settings.BullseyeLatitudeDeg = latitude;
        Settings.BullseyeLongitudeDeg = longitude;
        Settings.ShowBullseye = true;
        _configStore.SaveDisplaySettings(Settings);
        UpdateBullseyeText();
        Raise(nameof(BullseyeButtonActive));
        Raise(nameof(Settings));
        return true;
    }

    private void ClearBullseye()
    {
        Settings.ShowBullseye = false;
        ShowBullseyePopup = false;
        _configStore.SaveDisplaySettings(Settings);
        InitializeBullseyeText();
        Raise(nameof(BullseyeButtonActive));
        Raise(nameof(Settings));
    }

    private void CancelBullseye()
    {
        ShowBullseyePopup = false;
        InitializeBullseyeText();
        Raise(nameof(BullseyeButtonActive));
        Raise(nameof(Settings));
    }

    private void UpdateBullseyeText()
    {
        BullseyeText = Settings.ShowBullseye &&
            Settings.BullseyeLatitudeDeg.HasValue &&
            Settings.BullseyeLongitudeDeg.HasValue
                ? $"Bullseye: {Settings.BullseyeLatitudeDeg.Value:0.####}, {Settings.BullseyeLongitudeDeg.Value:0.####}"
                : "Bullseye: off";
    }

    private static bool TryParseCoordinate(string text, double min, double max, string allowedHemisphereLetters, out double value)
    {
        value = 0;
        var normalized = text.Trim().ToUpperInvariant().Replace(',', '.');
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return false;
        }

        double sign = 1;
        foreach (var hemisphere in allowedHemisphereLetters)
        {
            var index = normalized.IndexOf(hemisphere);
            if (index < 0)
            {
                continue;
            }

            if (normalized.LastIndexOf(hemisphere) != index)
            {
                return false;
            }

            sign = hemisphere is 'S' or 'W' ? -1 : 1;
            normalized = normalized.Remove(index, 1).Trim();
        }

        if (normalized.Any(char.IsLetter))
        {
            return false;
        }

        if (!double.TryParse(normalized, NumberStyles.Float, CultureInfo.InvariantCulture, out value))
        {
            return false;
        }

        value *= sign;
        return value >= min &&
            value <= max;
    }

    private void OpenDebugLogFolder()
    {
        try
        {
            DataSourceDebugLog.EnsureLogDirectoryExists();
            var logPath = DataSourceDebugLog.CurrentLogFilePath;
            var targetPath = File.Exists(logPath)
                ? $"/select,\"{logPath}\""
                : $"\"{DataSourceDebugLog.CurrentLogDirectoryPath}\"";

            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = targetPath,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"Could not open debug log folder.\n\n{ex.Message}",
                "Open Debug Log Folder",
                MessageBoxButton.OK,
            MessageBoxImage.Error);
        }
    }

    private void ClearWebViewCookies()
    {
        try
        {
            var cookiePath = Path.Combine(AppDataPaths.WebViewUserDataDirectory, "Default", "Network", "Cookies");
            var cookieJournalPath = $"{cookiePath}-journal";
            var deleted = false;

            if (File.Exists(cookiePath))
            {
                File.Delete(cookiePath);
                deleted = true;
            }

            if (File.Exists(cookieJournalPath))
            {
                File.Delete(cookieJournalPath);
                deleted = true;
            }

            MessageBox.Show(
                deleted
                    ? "WebView cookies cleared. Restart the app if a page still shows an existing session."
                    : "No WebView cookies were found.",
                "Clear WebView Cookies",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"Could not clear WebView cookies.\n\nClose any active WebView page and try again.\n\n{ex.Message}",
                "Clear WebView Cookies",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private void OnConnectionChanged(object? _, bool connected)
    {
        if (!Application.Current.Dispatcher.CheckAccess())
        {
            Application.Current.Dispatcher.Invoke(() => OnConnectionChanged(_, connected));
            return;
        }

        SimConnected = connected;
        Raise(nameof(XPlane12AarStatusText));
        Aar.NotifyContextChanged();
        ConnectionText = connected ? "Connected" : "Disconnected";
        var simMode = DataSourceModes.UsesSimulatorConnection(Settings.DataSourceMode);
        SimConnectText = simMode ? (connected ? "Connected" : "Disconnected") : "N/A (Demo)";
        if (IsTacticalLinkConnected)
            TacticalLinkMessage = connected
                ? "Connected. Ownship sharing is active."
                : "Connected. Waiting for simulator telemetry.";
        if (!simMode)
        {
            return;
        }

        if (connected)
        {
            _showSimConnectDebugOnFailure = false;
            _simConnectCheckingStartedAt = null;
            return;
        }

        if (DataSourceModes.IsAnyXPlane(Settings.DataSourceMode))
        {
            return;
        }

        if (_showSimConnectDebugOnFailure)
        {
            if (SuppressModalDialogs)
            {
                _showSimConnectDebugOnFailure = false;
                return;
            }

            _showSimConnectDebugOnFailure = false;
            if (!SimConnectTrafficFeed.CanUseDll(Settings.PreferredSimConnectDllPath))
            {
                Settings.PreferredSimConnectDllPath = null;
                _configStore.SaveDisplaySettings(Settings);
            }

            var dialog = new SimConnectDebugDialog(
                "SimConnect connection could not be established.\n\n" +
                SimConnectTrafficFeed.BuildDiagnosticReport(Settings))
            {
                Owner = Application.Current.MainWindow
            };

            dialog.ShowDialog();

            if (dialog.Choice == SimConnectDebugChoice.SelectMsfs)
            {
                _ = SelectMsfsExeAndReconnectAsync();
            }
            else if (dialog.Choice == SimConnectDebugChoice.SelectDll)
            {
                _ = SelectSimConnectDllAndReconnectAsync();
            }
        }
    }

    private async Task SwitchDataSourceAsync(string source, bool forceRestart)
    {
        source = DataSourceModes.Normalize(source);
        if (!forceRestart && string.Equals(Settings.DataSourceMode, source, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        DataSourceDebugLog.Info("App", $"Switching data source | from={Settings.DataSourceMode} to={source} forceRestart={forceRestart}");

        _feed.ConnectionChanged -= OnConnectionChanged;
        _feed.SnapshotReceived -= OnSnapshotReceived;
        if (_feed is IAarPoseSource oldPoseSource) oldPoseSource.AarPoseSampled -= OnAarPoseSampled;
        if (_feed is IAarFuelAdapter oldFuelAdapter) oldFuelAdapter.FuelSampled -= OnAarFuelSampled;
        if (_feed is IAarBridgeRuntimeStatusSource oldBridgeStatus) oldBridgeStatus.BridgeRuntimeStateChanged -= OnAarBridgeRuntimeStateChanged;
        await _feed.StopAsync();
        await _feed.DisposeAsync();

        Settings.DataSourceMode = source;
        _repository = new TrafficRepository();
        Picture = null;
        TrafficText = "0 contacts";
        _refreshCounter = 0;
        _rateWindowStart = DateTimeOffset.UtcNow;
        RefreshRateText = "0.0 Hz";

        _feed = TrafficFeedFactory.Create(Settings, () => _tacticalLink.NearbyPeers);
        _feed.ConnectionChanged += OnConnectionChanged;
        _feed.SnapshotReceived += OnSnapshotReceived;
        if (_feed is IAarPoseSource newPoseSource) newPoseSource.AarPoseSampled += OnAarPoseSampled;
        if (_feed is IAarFuelAdapter newFuelAdapter) newFuelAdapter.FuelSampled += OnAarFuelSampled;
        if (_feed is IAarBridgeRuntimeStatusSource newBridgeStatus) newBridgeStatus.BridgeRuntimeStateChanged += OnAarBridgeRuntimeStateChanged;
        RefreshMsfsAarBridgeStatus();
        Aar.NotifyContextChanged();
        UpdateAarSampling();
        await _feed.StartAsync(_runCts.Token);

        UpdateSourceState(Settings.DataSourceMode);
    }

    private async Task SelectMsfsExeAndReconnectAsync()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Select MSFS executable",
            Filter = "MSFS executable|FlightSimulator2024.exe;FlightSimulator.exe;*.exe",
            CheckFileExists = true,
            Multiselect = false
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        Settings.MsfsExePath = dialog.FileName;
        Settings.PreferredSimConnectDllPath = SimConnectTrafficFeed.TryResolveDllFromMsfsExe(dialog.FileName);
        if (string.IsNullOrWhiteSpace(Settings.PreferredSimConnectDllPath))
        {
            var dllDialog = new OpenFileDialog
            {
                Title = "Select SimConnect DLL",
                Filter = "SimConnect DLL|SimConnect.dll|DLL files|*.dll",
                CheckFileExists = true,
                Multiselect = false
            };

            if (dllDialog.ShowDialog() == true)
            {
                Settings.PreferredSimConnectDllPath = dllDialog.FileName;
            }
        }

        _configStore.SaveDisplaySettings(Settings);
        await SwitchDataSourceAsync(DataSourceModes.Msfs, forceRestart: true);
    }

    private async Task SelectSimConnectDllAndReconnectAsync()
    {
        var dllDialog = new OpenFileDialog
        {
            Title = "Select SimConnect DLL",
            Filter = "SimConnect DLL|SimConnect.dll|DLL files|*.dll",
            CheckFileExists = true,
            Multiselect = false
        };

        if (dllDialog.ShowDialog() != true)
        {
            return;
        }

        if (!SimConnectTrafficFeed.CanUseDll(dllDialog.FileName))
        {
            MessageBox.Show(
                "Selected DLL cannot be loaded by this x64 app.\nUse a compatible SimConnect DLL (SDK or sim runtime).",
                "Invalid SimConnect DLL",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            return;
        }

        Settings.PreferredSimConnectDllPath = dllDialog.FileName;
        _configStore.SaveDisplaySettings(Settings);
        await SwitchDataSourceAsync(DataSourceModes.Msfs, forceRestart: true);
    }

    private void UpdateSimConnectCheckingTimeout()
    {
        if (!DataSourceModes.IsMsfs(Settings.DataSourceMode))
        {
            return;
        }

        if (!string.Equals(SimConnectText, "Checking...", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (_simConnectCheckingStartedAt is null)
        {
            _simConnectCheckingStartedAt = DateTimeOffset.UtcNow;
            return;
        }

        if ((DateTimeOffset.UtcNow - _simConnectCheckingStartedAt.Value).TotalSeconds < 8)
        {
            return;
        }

        SimConnectText = "Disconnected (timeout)";
        ConnectionText = "Disconnected";
        if (_showSimConnectDebugOnFailure)
        {
            _showSimConnectDebugOnFailure = false;
            MessageBox.Show(
                "SimConnect stayed in checking state too long.\n\n" +
                SimConnectTrafficFeed.BuildDiagnosticReport(Settings),
                "SimConnect Timeout Debug",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private void OnSnapshotReceived(object? _, TrafficSnapshot snapshot)
    {
        if (!Application.Current.Dispatcher.CheckAccess())
        {
            Application.Current.Dispatcher.Invoke(() => OnSnapshotReceived(_, snapshot));
            return;
        }

        _repository.ApplySnapshot(snapshot, _classification, Settings);
        _latestOwnship = snapshot.Ownship;
        if (DataSourceModes.IsMsfs(Settings.DataSourceMode))
        {
            LogMsfsClosureDiagnostics(snapshot);
        }
        TrafficText = $"{_repository.Count} contacts";
        _refreshCounter++;
    }

    private void OnTacticalLinkStateChanged(object? sender, EventArgs e)
    {
        if (!Application.Current.Dispatcher.CheckAccess())
        {
            Application.Current.Dispatcher.BeginInvoke(() => OnTacticalLinkStateChanged(sender, e));
            return;
        }
        if (!IsTacticalLinkConnected) _repository.RemoveTacticalLinkContacts();
        UpdateAarSampling();
        Aar.NotifyContextChanged();
        Raise(nameof(TacticalLinkStatusText));
        Raise(nameof(TacticalLinkIdentityText));
        Raise(nameof(TacticalLinkAircraftTypeText));
        Raise(nameof(IsCloudSignedIn));
        Raise(nameof(TacticalLinkPeerCountText));
        Raise(nameof(TacticalLinkPeers));
        Raise(nameof(TacticalLinkState));
        Raise(nameof(IsTacticalLinkConnected));
        Raise(nameof(XPlane12AarStatusText));
        if (!IsTacticalLinkConnected) _latestOwnship = null;
    }

    private void OnCloudAuthStateChanged(object? sender, AuthState state)
    {
        if (!Application.Current.Dispatcher.CheckAccess())
        {
            Application.Current.Dispatcher.BeginInvoke(() => OnCloudAuthStateChanged(sender, state));
            return;
        }
        Raise(nameof(TacticalLinkIdentityText));
        Raise(nameof(TacticalLinkAircraftTypeText));
        Raise(nameof(IsCloudSignedIn));
        if (state.Status is AuthStatus.SignedOut or AuthStatus.SessionExpired && _tacticalLink.ConnectionState != TacticalLinkConnectionState.Disconnected)
            _ = DisconnectTacticalLinkAsync();
    }

    private void ToggleTacticalLinkMenu()
    {
        ShowSettings = false;
        ShowTacticalLinkMenu = !ShowTacticalLinkMenu;
        Raise(nameof(ShowUtilityPanel));
        Raise(nameof(TacticalLinkIdentityText));
        Raise(nameof(TacticalLinkAircraftTypeText));
    }

    private async Task ConnectTacticalLinkAsync()
    {
        TacticalLinkMessage = "Connecting to TacticalLink...";
        try
        {
            await _tacticalLink.ConnectAsync(_runCts.Token, TacticalLinkAutoReconnect, TacticalLinkInterestRadiusNm, TacticalLinkDebugDiagnostics);
            TacticalLinkMessage = DataSourceModes.UsesSimulatorConnection(Settings.DataSourceMode) && _feed.IsConnected
                ? "Connected. Ownship sharing is active."
                : "Connected. Ownship sharing starts when a simulator feed is connected.";
        }
        catch (CloudApiException ex)
        {
            _tacticalLink.MarkConnectionFailed();
            TacticalLinkMessage = ex.Message;
            DataSourceDebugLog.Warn("TacticalLink", $"Connection could not be established | {ex.Message}");
        }
        catch (Exception ex)
        {
            _tacticalLink.MarkConnectionFailed();
            TacticalLinkMessage = "TacticalLink is unavailable. VTSD will continue normally.";
            DataSourceDebugLog.Warn("TacticalLink", $"Connection failed | {ex.Message}");
        }
        Raise(nameof(TacticalLinkIdentityText));
        Raise(nameof(TacticalLinkAircraftTypeText));
    }

    private async Task DisconnectTacticalLinkAsync()
    {
        await _tacticalLink.DisconnectAsync();
        TacticalLinkMessage = "Disconnected. Ownship sharing has stopped.";
        _repository.RemoveTacticalLinkContacts();
    }

    private void OnAarViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AarViewModel.AarFuelUnit)) _configStore.SaveDisplaySettings(Settings);
        Raise($"Aar.{e.PropertyName}");
    }
    private void SelectMsfs2024CommunityFolder()
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Select the MSFS 2024 Community2024 folder",
            InitialDirectory = Directory.Exists(Settings.Msfs2024CommunityFolder) ? Settings.Msfs2024CommunityFolder : string.Empty,
            Multiselect = false
        };
        if (dialog.ShowDialog() != true) return;
        var resolved = _msfsPackagePathResolver.ResolveCommunity2024(dialog.FolderName, []);
        if (resolved is null)
        {
            MsfsAarBridgeStatusText = "Select the existing Community2024 folder inside the MSFS 2024 package directory.";
            return;
        }
        Settings.Msfs2024CommunityFolder = resolved;
        _configStore.SaveDisplaySettings(Settings);
        RefreshMsfsAarBridgeStatus();
    }

    private async Task InstallMsfsAarBridgeAsync()
    {
        var community = _msfsPackagePathResolver.ResolveCommunity2024(Settings.Msfs2024CommunityFolder);
        if (community is null)
        {
            MsfsAarBridgeStatusText = "MSFS 2024 Community2024 folder could not be located. Select it first.";
            return;
        }
        try
        {
            var package = await _msfsAarBridgeInstaller.InstallBundledAsync(typeof(MainViewModel).Assembly, community, _runCts.Token);
            Settings.Msfs2024CommunityFolder = community;
            _configStore.SaveDisplaySettings(Settings);
            MsfsAarBridgeStatusText = $"Bridge {package.Version} installed. Restart Microsoft Flight Simulator 2024 to load it.";
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException)
        {
            MsfsAarBridgeStatusText = $"Bridge installation failed: {ex.Message}";
        }
    }

    private void UninstallMsfsAarBridge()
    {
        var community = _msfsPackagePathResolver.ResolveCommunity2024(Settings.Msfs2024CommunityFolder);
        if (community is null)
        {
            MsfsAarBridgeStatusText = "MSFS 2024 Community2024 folder could not be located.";
            return;
        }
        try
        {
            MsfsAarBridgeStatusText = _msfsAarBridgeInstaller.Uninstall(community)
                ? "MSFS AAR Bridge removed. Restart Microsoft Flight Simulator 2024 if it is running."
                : "MSFS AAR Bridge is not installed.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            MsfsAarBridgeStatusText = $"Bridge uninstall failed: {ex.Message}";
        }
    }

    private void RefreshMsfsAarBridgeStatus()
    {
        var community = _msfsPackagePathResolver.ResolveCommunity2024(Settings.Msfs2024CommunityFolder);
        if (community is null)
        {
            MsfsAarBridgeStatusText = "MSFS 2024 Community2024 folder not found. Select the folder to manage the bridge.";
            return;
        }
        Settings.Msfs2024CommunityFolder = community;
        var installation = _msfsAarBridgeInstaller.InspectInstallation(community);
        var installationState = installation.State;
        var installed = installation.Package;
        var bridgeStatus = _feed as IAarBridgeRuntimeStatusSource;
        var runtimeState = bridgeStatus?.BridgeRuntimeState;
        var installedVersion = installed?.Version;
        var runningVersion = bridgeStatus?.BridgeVersion;
        var version = runningVersion ?? installedVersion;
        var name = version is null ? "MSFS AAR Bridge" : $"MSFS AAR Bridge {version}";
        if (installationState == AarBridgeInstallationState.Invalid && runtimeState is not (AarBridgeRuntimeState.ConnectedReadOnly or AarBridgeRuntimeState.ConnectedWritable))
        {
            MsfsAarBridgeStatusText = "An invalid MSFS AAR Bridge package is present. Use INSTALL / UPDATE to repair it; live fuel transfer is unavailable. Dry Hookup remains available.";
            return;
        }
        if (installationState == AarBridgeInstallationState.NotInstalled && runtimeState is not (AarBridgeRuntimeState.ConnectedReadOnly or AarBridgeRuntimeState.ConnectedWritable))
        {
            MsfsAarBridgeStatusText = "AAR Bridge not installed. Live fuel transfer requires the bridge; Dry Hookup does not.";
            return;
        }
        if (runtimeState is { } state)
        {
            var adapter = _feed as IAarFuelAdapter;
            var fuelRead = adapter?.CanReadFuel == true ? "ready" : "unavailable";
            var fuelWrite = adapter?.CanWriteFuel == true ? "ready" : "unavailable";
            var versionMismatch = installedVersion is not null && runningVersion is not null &&
                !string.Equals(installedVersion, runningVersion, StringComparison.OrdinalIgnoreCase);
            var restartNotice = versionMismatch ? $" Installed {installedVersion}, running {runningVersion}; restart MSFS 2024 to activate the installed version." : string.Empty;
            var invalidPackageNotice = installationState == AarBridgeInstallationState.Invalid
                ? " The package on disk is invalid; live fuel transfer will stop after an MSFS restart. Repair it with INSTALL / UPDATE."
                : string.Empty;
            MsfsAarBridgeStatusText = state switch
            {
                AarBridgeRuntimeState.NotInstalled => "AAR Bridge not installed. Live fuel transfer requires the bridge; Dry Hookup does not." + invalidPackageNotice,
                AarBridgeRuntimeState.InstalledNotRunning => $"{name} installed. Restart MSFS 2024 to activate it.{invalidPackageNotice}",
                AarBridgeRuntimeState.Connecting => $"{name} connecting to MSFS 2024.{invalidPackageNotice}",
                AarBridgeRuntimeState.ConnectedReadOnly => $"{name} connected. Fuel read {fuelRead}; fuel write unavailable.{restartNotice} {bridgeStatus?.BridgeDiagnostic ?? "Dry Hookup remains available."}{invalidPackageNotice}",
                AarBridgeRuntimeState.ConnectedWritable => $"{name} connected. Fuel read {fuelRead}; fuel write {fuelWrite}.{restartNotice}{invalidPackageNotice}",
                AarBridgeRuntimeState.ProtocolMismatch => $"{name} protocol is incompatible. Update the bridge.{restartNotice} {bridgeStatus?.BridgeDiagnostic}{invalidPackageNotice}",
                _ => $"{name} error. {bridgeStatus?.BridgeDiagnostic ?? "Fuel transfer is stopped; Dry Hookup remains available."}{invalidPackageNotice}"
            };
            return;
        }
        MsfsAarBridgeStatusText = installationState switch
        {
            AarBridgeInstallationState.NotInstalled => "AAR Bridge not installed. Live fuel transfer requires the bridge; Dry Hookup does not.",
            AarBridgeInstallationState.Invalid => "An invalid MSFS AAR Bridge package is present. Use INSTALL / UPDATE to repair it; live fuel transfer is unavailable. Dry Hookup remains available.",
            _ => $"{name} installed. Start MSFS 2024 to connect the runtime bridge."
        };
    }

    private void OnAarBridgeRuntimeStateChanged(object? sender, AarBridgeRuntimeState state)
    {
        if (!Application.Current.Dispatcher.CheckAccess())
        {
            Application.Current.Dispatcher.BeginInvoke(() => OnAarBridgeRuntimeStateChanged(sender, state));
            return;
        }
        RefreshMsfsAarBridgeStatus();
        Aar.NotifyContextChanged();
    }

    private void OnAarPoseSampled(object? sender, OwnshipState sample) => Aar.OnPose(sample);

    private void OnAarFuelSampled(object? sender, AarFuelReading reading)
    {
        if (!Application.Current.Dispatcher.CheckAccess())
        {
            Application.Current.Dispatcher.BeginInvoke(() => OnAarFuelSampled(sender, reading));
            return;
        }
        Aar.OnFuel(reading);
        Aar.NotifyContextChanged();
        Raise(nameof(XPlane12AarStatusText));
    }

    private void UpdateAarSampling()
    {
        Aar.SetConnectedOperationSampling(IsTacticalLinkConnected, _feed as IAarPoseSource);
    }

    private void PublishLatestTacticalTelemetry()
    {
        if (!IsTacticalLinkConnected || !DataSourceModes.UsesSimulatorConnection(Settings.DataSourceMode) || !_feed.IsConnected || _latestOwnship is not { } ownship) return;
        _ = _tacticalLink.PublishTelemetryAsync(ownship, _runCts.Token);
    }

    private void LogMsfsClosureDiagnostics(TrafficSnapshot snapshot)
    {
        var picture = _repository.BuildPicture(Settings);
        foreach (var target in picture.Targets)
        {
            var tracked = _repository.GetTrackedContact(target.Id);
            if (tracked is null)
            {
                continue;
            }

            DataSourceDebugLog.ThrottledDebug(
                "MSFS",
                $"closure-{target.Id}",
                TimeSpan.FromSeconds(3),
                () => $"MSFS closure | objectId={target.Id}@{tracked.Generation} callsign={target.DisplayName} targetAgeSec={tracked.TargetSampleAgeSeconds:0.000} ownshipAgeSec={tracked.OwnshipSampleAgeSeconds:0.000} timestampSkewSec={tracked.TimestampSkewSeconds:0.000} displayGeometry={tracked.DisplayGeometryMode} closureAlignment={tracked.AlignmentMethod} rangeSample={tracked.RangeSampleDecision} regressionSamples={tracked.RegressionSampleCount} regressionWindowSec={tracked.RegressionWindowSeconds:0.00} dataSpanSec={tracked.RegressionDataSpanSeconds:0.00} largestGapSec={tracked.LargestSampleGapSeconds:0.00} residualRmsNm={tracked.RegressionResidualRmsNm:0.0000} outliersRemoved={tracked.RegressionOutliersRemoved} rangeNm={target.RangeNm:0.00} vectorClosureKt={tracked.VectorClosureKt?.ToString("0", System.Globalization.CultureInfo.InvariantCulture) ?? "---"} vectorQuality={tracked.VectorQualityScore:0.00} positionClosureKt={tracked.PositionClosureKt?.ToString("0", System.Globalization.CultureInfo.InvariantCulture) ?? "---"} positionQuality={tracked.PositionQualityScore:0.00} disagreementKt={tracked.ClosureDisagreementKt?.ToString("0", System.Globalization.CultureInfo.InvariantCulture) ?? "---"} selectedClosureKt={target.ClosureKt?.ToString("0", System.Globalization.CultureInfo.InvariantCulture) ?? "---"} qualityScore={tracked.ClosureQualityScore:0.00} source={tracked.ClosureSource}");
        }
    }

    private void RefreshPicture()
    {
        if (_repository.Ownship is null)
        {
            return;
        }

        var basePicture = _repository.BuildPicture(Settings);
        var mappedTargets = basePicture.Targets.Select(ApplyManualOverride).ToList();
        Picture = new TacticalPicture(basePicture.Ownship, mappedTargets, basePicture.Timestamp);
        Raise(nameof(HeaderText));
        UpdateRefreshRate();
    }

    private async Task LoadAirspacesAsync()
    {
        try
        {
            var airspaces = await _airspaceDataService.LoadAsync(Settings, _runCts.Token);
            if (!Application.Current.Dispatcher.CheckAccess())
            {
                Application.Current.Dispatcher.Invoke(() => ApplyLoadedAirspaces(airspaces));
                return;
            }

            ApplyLoadedAirspaces(airspaces);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            AirspaceText = $"Airspace: unavailable ({ex.Message})";
            DataSourceDebugLog.Info("Airspace", $"Airspace load failed | {ex}");
        }
    }

    private void ApplyLoadedAirspaces(IReadOnlyList<AirspaceArea> airspaces)
    {
        Airspaces = airspaces;
        var activeCount = airspaces.Count(a => a.IsActive);
        AirspaceText = $"Airspace: {FormatAirspaceRegion(Settings.AirspaceFirCodes)} {activeCount} active";
    }

    private void UpdateRefreshRate()
    {
        var now = DateTimeOffset.UtcNow;
        var seconds = (now - _rateWindowStart).TotalSeconds;
        if (seconds < 1)
        {
            return;
        }

        var hz = _refreshCounter / seconds;
        RefreshRateText = $"{hz:0.0} Hz";
        _refreshCounter = 0;
        _rateWindowStart = now;
    }

    private static string ResolveConfigDirectory()
    {
        return AppDataPaths.ApplicationDataDirectory;
    }

    private static string FormatAirspaceRegion(IReadOnlyCollection<string> firCodes)
    {
        var values = firCodes
            .Where(static fir => !string.IsNullOrWhiteSpace(fir))
            .Select(static fir => fir.Trim().ToUpperInvariant())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (values.SetEquals(["EFIN", "EETT"]))
        {
            return "EFIN + EETT";
        }

        if (values.SetEquals(["EETT"]))
        {
            return "EETT";
        }

        return "EFIN";
    }

    private IEnumerable<AirspaceRegionOptionViewModel> CreateAirspaceRegionOptions()
    {
        var selected = Settings.AirspaceFirCodes.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var firCode in new[] { "efin", "eett" })
        {
            yield return new AirspaceRegionOptionViewModel(
                firCode,
                firCode.ToUpperInvariant(),
                selected.Contains(firCode),
                SetAirspaceRegionEnabled);
        }
    }

    private bool SetAirspaceRegionEnabled(string firCode, bool enabled)
    {
        var selected = Settings.AirspaceFirCodes
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Select(static value => value.Trim().ToLowerInvariant())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (enabled)
        {
            selected.Add(firCode);
        }
        else if (selected.Count > 1)
        {
            selected.Remove(firCode);
        }
        else
        {
            return false;
        }

        Settings.AirspaceFirCodes = selected
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        Raise(nameof(Settings));
        _ = LoadAirspacesAsync();
        return true;
    }

    public void CycleTargetAffiliation(string targetId)
    {
        if (!_manualTargetMetadata.TryGetValue(targetId, out var metadata))
        {
            metadata = new ManualTargetMetadata { Id = targetId, Affiliation = ManualAffiliation.Neutral };
            _manualTargetMetadata[targetId] = metadata;
        }

        metadata.Affiliation = metadata.Affiliation switch
        {
            ManualAffiliation.Neutral => ManualAffiliation.Friendly,
            ManualAffiliation.Friendly => ManualAffiliation.Enemy,
            _ => ManualAffiliation.Neutral
        };

        _configStore.SaveManualTargetMetadata(_manualTargetMetadata);
        Raise(nameof(ManualTargetMetadata));
        RefreshPicture();
    }

    public bool TrySelectInterceptTarget(string targetId)
    {
        if (!InterceptSelectionArmed)
        {
            return false;
        }

        ToggleInterceptTarget(targetId);
        InterceptSelectionArmed = false;
        return true;
    }

    public void ToggleInterceptTarget(string targetId)
    {
        InterceptTargetId = string.Equals(InterceptTargetId, targetId, StringComparison.OrdinalIgnoreCase)
            ? null
            : targetId;
        RefreshPicture();
    }

    public string? GetManualName(string targetId) =>
        _manualTargetMetadata.TryGetValue(targetId, out var metadata) ? metadata.DisplayName : null;

    public void SetManualName(string targetId, string? name)
    {
        if (!_manualTargetMetadata.TryGetValue(targetId, out var metadata))
        {
            metadata = new ManualTargetMetadata { Id = targetId };
            _manualTargetMetadata[targetId] = metadata;
        }

        metadata.DisplayName = string.IsNullOrWhiteSpace(name) ? null : name.Trim();
        _configStore.SaveManualTargetMetadata(_manualTargetMetadata);
        Raise(nameof(ManualTargetMetadata));
        RefreshPicture();
    }

    public void SetTargetLabelOffset(string targetId, double offsetX, double offsetY)
    {
        if (!_manualTargetMetadata.TryGetValue(targetId, out var metadata))
        {
            metadata = new ManualTargetMetadata { Id = targetId };
            _manualTargetMetadata[targetId] = metadata;
        }

        metadata.LabelOffsetX = Math.Abs(offsetX) < 0.5 ? 0 : offsetX;
        metadata.LabelOffsetY = Math.Abs(offsetY) < 0.5 ? 0 : offsetY;
        _configStore.SaveManualTargetMetadata(_manualTargetMetadata);
        Raise(nameof(ManualTargetMetadata));
        RefreshPicture();
    }

    public void SetWebDisplayStatus(string status)
    {
        WebDisplayText = status;
    }

    public void ToggleTargetLabelVisibility(string targetId)
    {
        if (!_manualTargetMetadata.TryGetValue(targetId, out var metadata))
        {
            metadata = new ManualTargetMetadata { Id = targetId };
            _manualTargetMetadata[targetId] = metadata;
        }

        metadata.LabelHidden = !metadata.LabelHidden;
        _configStore.SaveManualTargetMetadata(_manualTargetMetadata);
        Raise(nameof(ManualTargetMetadata));
        RefreshPicture();
    }

    private void UpdateSourceState(string sourceMode)
    {
        Settings.DataSourceMode = DataSourceModes.Normalize(sourceMode);
        SelectedDataSource = Settings.DataSourceMode;
        SourceText = $"Source: {Settings.DataSourceMode}";
        Raise(nameof(SimulatorStatusLabel));
        Raise(nameof(SimulatorFooterText));
        Raise(nameof(DataSourceDebugLoggingEnabled));
        Raise(nameof(DiagnosticTelemetryEnabled));
        Raise(nameof(WebServerEnabled));
        Raise(nameof(VatsimCallsignLookupEnabled));

        if (!DataSourceModes.UsesSimulatorConnection(Settings.DataSourceMode))
        {
            SimConnectText = "N/A (Demo)";
            _showSimConnectDebugOnFailure = false;
            _simConnectCheckingStartedAt = null;
            return;
        }

        SimConnectText = "Checking...";
        _showSimConnectDebugOnFailure = DataSourceModes.IsMsfs(Settings.DataSourceMode);
        _simConnectCheckingStartedAt = DateTimeOffset.UtcNow;
    }

    private ComputedTarget ApplyManualOverride(ComputedTarget target)
    {
        if (!_manualTargetMetadata.TryGetValue(target.Id, out var metadata))
        {
            return target;
        }

        var displayName = string.IsNullOrWhiteSpace(metadata.DisplayName) ? target.DisplayName : metadata.DisplayName!;
        var category = metadata.Affiliation switch
        {
            ManualAffiliation.Friendly => TargetCategory.Friend,
            ManualAffiliation.Enemy => TargetCategory.Enemy,
            _ => target.Category is TargetCategory.Friend or TargetCategory.Enemy ? TargetCategory.Unknown : target.Category
        };

        return target with { DisplayName = displayName, Category = category };
    }
}

public sealed record TacticalLinkPeerDisplay(string ParticipantId, string Callsign, string AircraftType, string RangeText, string StatusText);

public sealed class AirspaceRegionOptionViewModel : ViewModelBase
{
    private readonly Func<string, bool, bool> _setEnabled;
    private bool _isEnabled;

    public AirspaceRegionOptionViewModel(
        string firCode,
        string displayName,
        bool isEnabled,
        Func<string, bool, bool> setEnabled)
    {
        FirCode = firCode;
        DisplayName = displayName;
        _isEnabled = isEnabled;
        _setEnabled = setEnabled;
    }

    public string FirCode { get; }
    public string DisplayName { get; }

    public bool IsEnabled
    {
        get => _isEnabled;
        set
        {
            if (_isEnabled == value)
            {
                return;
            }

            if (!_setEnabled(FirCode, value))
            {
                Raise();
                return;
            }

            _isEnabled = value;
            Raise();
        }
    }
}

public sealed record CloudKneepadPageViewModel(
    string Key,
    string CollectionSlug,
    string CollectionName,
    string Title,
    string? Category,
    string ContentMarkdown,
    Uri DashboardUri);

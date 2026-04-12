using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using Microsoft.Win32;
using System.Windows;
using System.Windows.Threading;
using TacticalDisplay.App.Commands;
using TacticalDisplay.App.Data;
using TacticalDisplay.App.Services;
using TacticalDisplay.Core.Config;
using TacticalDisplay.Core.Models;
using TacticalDisplay.Core.Services;

namespace TacticalDisplay.App.ViewModels;

public sealed class MainViewModel : ViewModelBase, IAsyncDisposable
{
    private readonly JsonConfigStore _configStore;
    private readonly ClassificationConfig _classification;
    private readonly AirspaceDataService _airspaceDataService = new();
    private readonly AirportDataService _airportDataService;
    private readonly NavaidDataService _navaidDataService;
    private TrafficRepository _repository = new();
    private ITrafficDataFeed _feed;
    private readonly DispatcherTimer _renderTimer;
    private readonly DispatcherTimer _airspaceTimer;
    private readonly CancellationTokenSource _runCts = new();
    private TacticalPicture? _picture;
    private IReadOnlyList<AirspaceArea> _airspaces = [];
    private IReadOnlyList<AirportMapPoint> _airports = [];
    private IReadOnlyList<NavaidMapPoint> _navaids = [];
    private string _connectionText = "Disconnected";
    private string _simConnectText = "N/A";
    private string _trafficText = "0 contacts";
    private string _sourceText = string.Empty;
    private string _airspaceText = "Airspace: loading";
    private string _airportText = "Airports: loading";
    private string _navaidText = "Navaids: loading";
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
    private bool _showSettings = true;
    private bool _isAlwaysOnTop;
    private readonly Dictionary<string, ManualTargetMetadata> _manualTargetMetadata;
    private bool _showSimConnectDebugOnFailure;
    private DateTimeOffset? _simConnectCheckingStartedAt;

    public MainViewModel()
    {
        var configPath = ResolveConfigDirectory();
        DataSourceDebugLog.Info("App", $"Application startup | configDir={configPath} logFile={DataSourceDebugLog.CurrentLogFilePath}");
        _configStore = new JsonConfigStore(configPath);
        _airportDataService = new AirportDataService(configPath);
        _navaidDataService = new NavaidDataService(configPath);
        Settings = _configStore.LoadDisplaySettings();
        DataSourceDebugLog.SetEnabled(Settings.EnableDataSourceDebugLogging);
        DataSourceDebugLog.Info("App", $"Data source debug logging enabled={Settings.EnableDataSourceDebugLogging}");
        _classification = _configStore.LoadClassification();
        _manualTargetMetadata = _configStore.LoadManualTargetMetadata();
        Settings.DataSourceMode = DataSourceModes.Normalize(Settings.DataSourceMode);
        _feed = TrafficFeedFactory.Create(Settings);
        _feed.ConnectionChanged += OnConnectionChanged;
        _feed.SnapshotReceived += OnSnapshotReceived;

        ToggleOrientationCommand = new RelayCommand(ToggleOrientation);
        IncreaseRangeCommand = new RelayCommand(IncreaseRange);
        DecreaseRangeCommand = new RelayCommand(DecreaseRange);
        ToggleDeclutterCommand = new RelayCommand(ToggleDeclutter);
        ToggleMapCommand = new RelayCommand(ToggleMap);
        ToggleAirportsCommand = new RelayCommand(ToggleAirports);
        ToggleNavaidsCommand = new RelayCommand(ToggleNavaids);
        ToggleAirspaceCommand = new RelayCommand(ToggleAirspace);
        ToggleLabelsCommand = new RelayCommand(ToggleLabels);
        ToggleTrailsCommand = new RelayCommand(ToggleTrails);
        ApplyBullseyeCommand = new RelayCommand(ApplyBullseye);
        ClearBullseyeCommand = new RelayCommand(ClearBullseye);
        SaveSettingsCommand = new RelayCommand(() => _configStore.SaveDisplaySettings(Settings));
        ApplyDataSourceCommand = new RelayCommand(ApplyDataSource);
        ToggleSettingsCommand = new RelayCommand(ToggleSettingsPanel);
        ToggleAlwaysOnTopCommand = new RelayCommand(ToggleAlwaysOnTop);
        OpenDebugLogFolderCommand = new RelayCommand(OpenDebugLogFolder);

        _renderTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1000.0 / Settings.RenderRateFps) };
        _renderTimer.Tick += (_, _) =>
        {
            RefreshPicture();
            UpdateSimConnectCheckingTimeout();
        };
        _renderTimer.Start();
        _ = _feed.StartAsync(_runCts.Token);
        _airspaceTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(45) };
        _airspaceTimer.Tick += (_, _) => _ = LoadAirspacesAsync();
        _airspaceTimer.Start();
        _ = LoadAirspacesAsync();
        _ = LoadAirportsAsync();
        _ = LoadNavaidsAsync();
        InitializeBullseyeText();
        XPlane12ApiBaseUrlText = Settings.XPlane12ApiBaseUrl;
        MinTrackedAltitudeFtText = Settings.MinTrackedAltitudeFt.ToString("0.##", CultureInfo.InvariantCulture);

        UpdateSourceState(Settings.DataSourceMode);
    }

    public TacticalDisplaySettings Settings { get; }
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

    public IReadOnlyList<AirportMapPoint> Airports
    {
        get => _airports;
        private set => SetField(ref _airports, value);
    }

    public IReadOnlyList<NavaidMapPoint> Navaids
    {
        get => _navaids;
        private set => SetField(ref _navaids, value);
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
        private set => SetField(ref _simConnectText, value);
    }

    public string SourceText
    {
        get => _sourceText;
        private set => SetField(ref _sourceText, value);
    }

    public string AirspaceText
    {
        get => _airspaceText;
        private set => SetField(ref _airspaceText, value);
    }

    public string AirportText
    {
        get => _airportText;
        private set => SetField(ref _airportText, value);
    }

    public string NavaidText
    {
        get => _navaidText;
        private set => SetField(ref _navaidText, value);
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

    public double AirportOpacityPercent
    {
        get => Math.Round(Settings.AirportOpacity * 100);
        set
        {
            var opacity = Math.Clamp(value / 100.0, 0.0, 1.0);
            if (Math.Abs(Settings.AirportOpacity - opacity) < 0.001)
            {
                return;
            }

            Settings.AirportOpacity = opacity;
            Raise();
            Raise(nameof(AirportOpacityText));
            Raise(nameof(Settings));
        }
    }

    public string AirportOpacityText => $"Airport opacity: {AirportOpacityPercent:0}%";

    public double NavaidOpacityPercent
    {
        get => Math.Round(Settings.NavaidOpacity * 100);
        set
        {
            var opacity = Math.Clamp(value / 100.0, 0.0, 1.0);
            if (Math.Abs(Settings.NavaidOpacity - opacity) < 0.001)
            {
                return;
            }

            Settings.NavaidOpacity = opacity;
            Raise();
            Raise(nameof(NavaidOpacityText));
            Raise(nameof(Settings));
        }
    }

    public string NavaidOpacityText => $"Navaid opacity: {NavaidOpacityPercent:0}%";

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
            DataSourceDebugLog.Info("App", $"Data source debug logging toggled | enabled={value}");
            Raise();
        }
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

    public bool IsAlwaysOnTop
    {
        get => _isAlwaysOnTop;
        private set
        {
            SetField(ref _isAlwaysOnTop, value);
            Raise(nameof(TopMostToggleText));
        }
    }

    public string SettingsToggleText => ShowSettings ? "Hide Settings" : "Show Settings";
    public string TopMostToggleText => IsAlwaysOnTop ? "Unpin Window" : "Pin On Top";
    public string DeclutterToggleText => Settings.Declutter ? "Declutter ON" : "Declutter OFF";
    public string MapToggleText => Settings.ShowMapLayer ? "Map ON" : "Map OFF";
    public string AirportsToggleText => Settings.ShowAirportLayer ? "Airports ON" : "Airports OFF";
    public string NavaidsToggleText => Settings.ShowNavaidLayer ? "Navaids ON" : "Navaids OFF";
    public string AirspaceToggleText => Settings.ShowAirspaceBoundaries ? "Areas ON" : "Areas OFF";
    public string TrailsToggleText => Settings.TrailsEnabled ? "Trails ON" : "Trails OFF";
    public bool ShowMsfsSettings => DataSourceModes.IsMsfs(SelectedDataSource);
    public bool ShowXPlane12Settings => DataSourceModes.IsXPlane12(SelectedDataSource);
    public bool ShowXPlaneLegacySettings => DataSourceModes.IsXPlaneLegacy(SelectedDataSource);
    public string SimulatorStatusLabel =>
        DataSourceModes.IsXPlane12(Settings.DataSourceMode) ? "X-Plane 12:" :
        DataSourceModes.IsXPlaneLegacy(Settings.DataSourceMode) ? "XPUIPC:" :
        "Simulator:";

    public ObservableCollection<int> RangeOptions { get; } = [10, 20, 40, 80, 120];
    public IReadOnlyDictionary<string, ManualTargetMetadata> ManualTargetMetadata => _manualTargetMetadata;
    public ObservableCollection<string> AvailableDataSources { get; } =
    [
        DataSourceModes.Demo,
        DataSourceModes.Msfs,
        DataSourceModes.XPlane12,
        DataSourceModes.XPlaneLegacy
    ];
    public RelayCommand ToggleOrientationCommand { get; }
    public RelayCommand IncreaseRangeCommand { get; }
    public RelayCommand DecreaseRangeCommand { get; }
    public RelayCommand ToggleDeclutterCommand { get; }
    public RelayCommand ToggleMapCommand { get; }
    public RelayCommand ToggleAirportsCommand { get; }
    public RelayCommand ToggleNavaidsCommand { get; }
    public RelayCommand ToggleAirspaceCommand { get; }
    public RelayCommand ToggleLabelsCommand { get; }
    public RelayCommand ToggleTrailsCommand { get; }
    public RelayCommand ApplyBullseyeCommand { get; }
    public RelayCommand ClearBullseyeCommand { get; }
    public RelayCommand SaveSettingsCommand { get; }
    public RelayCommand ApplyDataSourceCommand { get; }
    public RelayCommand ToggleSettingsCommand { get; }
    public RelayCommand ToggleAlwaysOnTopCommand { get; }
    public RelayCommand OpenDebugLogFolderCommand { get; }

    public string HeaderText =>
        $"{(Settings.OrientationMode == ScopeOrientationMode.NorthUp ? "N-UP" : "HDG-UP")}  |  RANGE {Settings.SelectedRangeNm} NM";

    public async ValueTask DisposeAsync()
    {
        DataSourceDebugLog.Info("App", "Application shutdown");
        try
        {
            _renderTimer.Stop();
            _airspaceTimer.Stop();
            _runCts.Cancel();
            await _feed.StopAsync();
            await _feed.DisposeAsync();
        }
        finally
        {
            _configStore.SaveDisplaySettings(Settings);
            _configStore.SaveManualTargetMetadata(_manualTargetMetadata);
            _runCts.Dispose();
        }
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

    private void ToggleDeclutter()
    {
        Settings.Declutter = !Settings.Declutter;
        Raise(nameof(DeclutterToggleText));
    }

    private void ToggleMap()
    {
        Settings.ShowMapLayer = !Settings.ShowMapLayer;
        Raise(nameof(MapToggleText));
        Raise(nameof(Settings));
    }

    private void ToggleAirports()
    {
        Settings.ShowAirportLayer = !Settings.ShowAirportLayer;
        Raise(nameof(AirportsToggleText));
        Raise(nameof(Settings));
    }

    private void ToggleNavaids()
    {
        Settings.ShowNavaidLayer = !Settings.ShowNavaidLayer;
        Raise(nameof(NavaidsToggleText));
        Raise(nameof(Settings));
    }

    private void ToggleAirspace()
    {
        Settings.ShowAirspaceBoundaries = !Settings.ShowAirspaceBoundaries;
        Raise(nameof(AirspaceToggleText));
    }

    private void ToggleLabels()
    {
        Settings.LabelMode = Settings.LabelMode switch
        {
            LabelMode.Full => LabelMode.Minimal,
            LabelMode.Minimal => LabelMode.Off,
            _ => LabelMode.Full
        };
    }

    private void ToggleTrails()
    {
        Settings.TrailsEnabled = !Settings.TrailsEnabled;
        Raise(nameof(TrailsToggleText));
    }

    private void ToggleSettingsPanel() => ShowSettings = !ShowSettings;

    private void ToggleAlwaysOnTop() => IsAlwaysOnTop = !IsAlwaysOnTop;

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
            UpdateBullseyeText();
            return;
        }

        BullseyeLatitudeText = Settings.BullseyeLatitudeDeg.Value.ToString("0.######", CultureInfo.InvariantCulture);
        BullseyeLongitudeText = Settings.BullseyeLongitudeDeg.Value.ToString("0.######", CultureInfo.InvariantCulture);
        UpdateBullseyeText();
    }

    private void ApplyBullseye()
    {
        if (!TryParseCoordinate(BullseyeLatitudeText, -90, 90, "NS", out var latitude) ||
            !TryParseCoordinate(BullseyeLongitudeText, -180, 180, "EW", out var longitude))
        {
            BullseyeText = "Bullseye: invalid coordinate";
            return;
        }

        Settings.BullseyeLatitudeDeg = latitude;
        Settings.BullseyeLongitudeDeg = longitude;
        Settings.ShowBullseye = true;
        _configStore.SaveDisplaySettings(Settings);
        UpdateBullseyeText();
        Raise(nameof(Settings));
    }

    private void ClearBullseye()
    {
        Settings.ShowBullseye = false;
        Settings.BullseyeLatitudeDeg = null;
        Settings.BullseyeLongitudeDeg = null;
        BullseyeLatitudeText = string.Empty;
        BullseyeLongitudeText = string.Empty;
        _configStore.SaveDisplaySettings(Settings);
        UpdateBullseyeText();
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

    private void OnConnectionChanged(object? _, bool connected)
    {
        if (!Application.Current.Dispatcher.CheckAccess())
        {
            Application.Current.Dispatcher.Invoke(() => OnConnectionChanged(_, connected));
            return;
        }

        SimConnected = connected;
        ConnectionText = connected ? "Connected" : "Disconnected";
        var simMode = DataSourceModes.UsesSimulatorConnection(Settings.DataSourceMode);
        SimConnectText = simMode ? (connected ? "Connected" : "Disconnected") : "N/A (Demo)";
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
        await _feed.StopAsync();
        await _feed.DisposeAsync();

        Settings.DataSourceMode = source;
        _repository = new TrafficRepository();
        Picture = null;
        TrafficText = "0 contacts";
        _refreshCounter = 0;
        _rateWindowStart = DateTimeOffset.UtcNow;
        RefreshRateText = "0.0 Hz";

        _feed = TrafficFeedFactory.Create(Settings);
        _feed.ConnectionChanged += OnConnectionChanged;
        _feed.SnapshotReceived += OnSnapshotReceived;
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
        TrafficText = $"{_repository.Count} contacts";
        _refreshCounter++;
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

    private async Task LoadAirportsAsync()
    {
        try
        {
            var airports = await _airportDataService.LoadAsync(_runCts.Token);
            if (!Application.Current.Dispatcher.CheckAccess())
            {
                Application.Current.Dispatcher.Invoke(() => ApplyLoadedAirports(airports));
                return;
            }

            ApplyLoadedAirports(airports);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            AirportText = $"Airports: unavailable ({ex.Message})";
            DataSourceDebugLog.Info("Airports", $"Airport load failed | {ex}");
        }
    }

    private void ApplyLoadedAirports(IReadOnlyList<AirportMapPoint> airports)
    {
        Airports = airports;
        AirportText = $"Airports: {airports.Count}";
    }

    private async Task LoadNavaidsAsync()
    {
        try
        {
            var navaids = await _navaidDataService.LoadAsync(_runCts.Token);
            if (!Application.Current.Dispatcher.CheckAccess())
            {
                Application.Current.Dispatcher.Invoke(() => ApplyLoadedNavaids(navaids));
                return;
            }

            ApplyLoadedNavaids(navaids);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            NavaidText = $"Navaids: unavailable ({ex.Message})";
            DataSourceDebugLog.Info("Navaids", $"Navaid load failed | {ex}");
        }
    }

    private void ApplyLoadedNavaids(IReadOnlyList<NavaidMapPoint> navaids)
    {
        Navaids = navaids;
        NavaidText = $"Navaids: {navaids.Count}";
    }

    private void ApplyLoadedAirspaces(IReadOnlyList<AirspaceArea> airspaces)
    {
        Airspaces = airspaces;
        var activeCount = airspaces.Count(a => a.IsActive);
        AirspaceText = $"Airspace: {Settings.AirspaceFirCode.ToUpperInvariant()} {activeCount} active";
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
        var baseDir = AppContext.BaseDirectory;
        var candidate = Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..", "config"));
        return Directory.Exists(candidate) ? candidate : Path.Combine(baseDir, "config");
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
        Raise(nameof(DataSourceDebugLoggingEnabled));

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

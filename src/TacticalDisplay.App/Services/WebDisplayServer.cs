using System.Globalization;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Windows.Threading;
using TacticalDisplay.App.ViewModels;
using TacticalDisplay.Core.Models;

namespace TacticalDisplay.App.Services;

public sealed class WebDisplayServer : IAsyncDisposable
{
    public const int DefaultPort = 8787;
    private static readonly TimeSpan CommandDispatchRefreshThreshold = TimeSpan.FromMilliseconds(1500);

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false
    };

    private readonly MainViewModel _viewModel;
    private readonly Dispatcher _dispatcher;
    private readonly int _port;
    private readonly CancellationTokenSource _cts = new();
    private TcpListener? _listener;
    private Task? _serverTask;

    public WebDisplayServer(MainViewModel viewModel, Dispatcher dispatcher, int port = DefaultPort)
    {
        _viewModel = viewModel;
        _dispatcher = dispatcher;
        _port = port;
    }

    public IReadOnlyList<string> LocalUrls { get; private set; } = [];

    public bool Start()
    {
        if (_listener is not null)
        {
            return true;
        }

        try
        {
            _listener = new TcpListener(IPAddress.Any, _port);
            _listener.Start();
            LocalUrls = ResolveLocalUrls(_port);
            _serverTask = Task.Run(() => AcceptLoopAsync(_cts.Token));
            DataSourceDebugLog.Info("Web", $"Tablet web display started | port={_port}");
            return true;
        }
        catch (Exception ex)
        {
            _listener = null;
            LocalUrls = [];
            DataSourceDebugLog.Info("Web", $"Tablet web display failed to start | port={_port} error={ex}");
            return false;
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        try
        {
            _listener?.Stop();
        }
        catch
        {
        }

        if (_serverTask is not null)
        {
            try
            {
                await _serverTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            catch (ObjectDisposedException)
            {
            }
        }

        _cts.Dispose();
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener!.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (Exception ex)
            {
                DataSourceDebugLog.Info("Web", $"Accept failed | {ex}");
                continue;
            }

            _ = Task.Run(() => HandleClientAsync(client, cancellationToken), CancellationToken.None);
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken cancellationToken)
    {
        using var _ = client;
        try
        {
            client.ReceiveTimeout = 3000;
            client.SendTimeout = 3000;
            using var stream = client.GetStream();
            var request = await ReadRequestAsync(stream, cancellationToken).ConfigureAwait(false);
            if (request is null)
            {
                return;
            }

            var (method, path, body) = request.Value;
            if (!string.Equals(method, "GET", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(method, "POST", StringComparison.OrdinalIgnoreCase))
            {
                await WriteResponseAsync(stream, 405, "Method Not Allowed", "text/plain; charset=utf-8", "Only GET and POST are supported.", cancellationToken).ConfigureAwait(false);
                return;
            }

            if (string.Equals(method, "GET", StringComparison.OrdinalIgnoreCase) && path is "/" or "/index.html")
            {
                await WriteResponseAsync(stream, 200, "OK", "text/html; charset=utf-8", IndexHtml, cancellationToken).ConfigureAwait(false);
                return;
            }

            if (string.Equals(method, "GET", StringComparison.OrdinalIgnoreCase) && path == "/api/snapshot")
            {
                var snapshot = CaptureSnapshot();
                var json = JsonSerializer.Serialize(snapshot, JsonOptions);
                await WriteResponseAsync(stream, 200, "OK", "application/json; charset=utf-8", json, cancellationToken).ConfigureAwait(false);
                return;
            }

            if (string.Equals(method, "GET", StringComparison.OrdinalIgnoreCase) && path == "/api/health")
            {
                await WriteResponseAsync(stream, 200, "OK", "application/json; charset=utf-8", "{\"ok\":true}", cancellationToken).ConfigureAwait(false);
                return;
            }

            if (string.Equals(method, "POST", StringComparison.OrdinalIgnoreCase) && path == "/api/command")
            {
                var command = ParseCommand(body);
                if (string.IsNullOrWhiteSpace(command))
                {
                    await WriteResponseAsync(stream, 400, "Bad Request", "application/json; charset=utf-8", "{\"ok\":false,\"error\":\"Missing command\"}", cancellationToken).ConfigureAwait(false);
                    return;
                }

                var result = ExecuteCommand(command);
                if (!result.Ok)
                {
                    var error = JsonSerializer.Serialize(new CommandResponse(false, result.Error ?? "Command failed"), JsonOptions);
                    await WriteResponseAsync(stream, 400, "Bad Request", "application/json; charset=utf-8", error, cancellationToken).ConfigureAwait(false);
                    return;
                }

                await WriteResponseAsync(stream, 200, "OK", "application/json; charset=utf-8", "{\"ok\":true}", cancellationToken).ConfigureAwait(false);
                return;
            }

            await WriteResponseAsync(stream, 404, "Not Found", "text/plain; charset=utf-8", "Not found.", cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException)
        {
        }
        catch (Exception ex)
        {
            DataSourceDebugLog.Info("Web", $"Request failed | {ex}");
        }
    }

    private static async Task<(string Method, string Path, string Body)?> ReadRequestAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        var buffer = new byte[8192];
        using var memory = new MemoryStream();
        var headerEnd = -1;
        while (memory.Length < 64 * 1024)
        {
            var length = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (length <= 0)
            {
                break;
            }

            memory.Write(buffer, 0, length);
            var textSoFar = Encoding.ASCII.GetString(memory.GetBuffer(), 0, (int)memory.Length);
            headerEnd = textSoFar.IndexOf("\r\n\r\n", StringComparison.Ordinal);
            if (headerEnd >= 0)
            {
                var contentLength = ParseContentLength(textSoFar[..headerEnd]);
                var expectedLength = headerEnd + 4 + contentLength;
                while (memory.Length < expectedLength)
                {
                    length = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                    if (length <= 0)
                    {
                        break;
                    }

                    memory.Write(buffer, 0, length);
                }

                break;
            }
        }

        if (memory.Length <= 0 || headerEnd < 0)
        {
            return null;
        }

        var bytes = memory.ToArray();
        var text = Encoding.ASCII.GetString(bytes, 0, headerEnd);
        var firstLineEnd = text.IndexOf("\r\n", StringComparison.Ordinal);
        if (firstLineEnd < 0)
        {
            return null;
        }

        var firstLine = text[..firstLineEnd];
        var parts = firstLine.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
        {
            return null;
        }

        var path = parts[1];
        var queryStart = path.IndexOf('?', StringComparison.Ordinal);
        if (queryStart >= 0)
        {
            path = path[..queryStart];
        }

        var bodyStart = headerEnd + 4;
        var body = bodyStart < bytes.Length ? Encoding.UTF8.GetString(bytes, bodyStart, bytes.Length - bodyStart) : string.Empty;
        return (parts[0], Uri.UnescapeDataString(path), body);
    }

    private static int ParseContentLength(string headers)
    {
        foreach (var line in headers.Split("\r\n", StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = line.IndexOf(':', StringComparison.Ordinal);
            if (separator <= 0)
            {
                continue;
            }

            if (line[..separator].Trim().Equals("Content-Length", StringComparison.OrdinalIgnoreCase) &&
                int.TryParse(line[(separator + 1)..].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var length))
            {
                return Math.Clamp(length, 0, 64 * 1024);
            }
        }

        return 0;
    }

    private static string? ParseCommand(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(body);
            return document.RootElement.TryGetProperty("command", out var commandElement) &&
                commandElement.ValueKind == JsonValueKind.String
                    ? commandElement.GetString()
                    : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static async Task WriteResponseAsync(
        NetworkStream stream,
        int statusCode,
        string reason,
        string contentType,
        string body,
        CancellationToken cancellationToken)
    {
        var bodyBytes = Encoding.UTF8.GetBytes(body);
        var header =
            $"HTTP/1.1 {statusCode} {reason}\r\n" +
            $"Content-Type: {contentType}\r\n" +
            $"Content-Length: {bodyBytes.Length.ToString(CultureInfo.InvariantCulture)}\r\n" +
            "Cache-Control: no-store\r\n" +
            "Connection: close\r\n" +
            "\r\n";
        var headerBytes = Encoding.ASCII.GetBytes(header);
        await stream.WriteAsync(headerBytes, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(bodyBytes, cancellationToken).ConfigureAwait(false);
    }

    private WebSnapshot CaptureSnapshot()
    {
        if (_dispatcher.HasShutdownStarted || _dispatcher.HasShutdownFinished)
        {
            return WebSnapshot.Unavailable("Application is shutting down");
        }

        try
        {
            if (_dispatcher.CheckAccess())
            {
                return BuildSnapshot();
            }

            return _dispatcher.Invoke(BuildSnapshot, DispatcherPriority.Send);
        }
        catch (Exception ex)
        {
            return WebSnapshot.Unavailable(ex.Message);
        }
    }

    private WebSnapshot BuildSnapshot()
    {
        var picture = _viewModel.Picture;
        var settings = _viewModel.Settings;
        if (picture is null)
        {
            return new WebSnapshot(
                false,
                "Waiting for ownship data",
                _viewModel.ConnectionText,
                _viewModel.SourceText,
                _viewModel.TrafficText,
                _viewModel.RefreshRateText,
                settings.SelectedRangeNm,
                settings.OrientationMode.ToString(),
                settings.LabelMode.ToString(),
                settings.MapOpacity,
                MapboxDefaults.ResolveAccessToken(),
                MapboxDefaults.ResolveDisplayStyleUrl(settings.ShowControlledAirspaceLayer),
                string.Empty,
                settings.Declutter,
                settings.ShowMapLayer,
                settings.ShowAirspaceBoundaries,
                settings.ShowControlledAirspaceLayer,
                settings.TrailsEnabled,
                settings.ShowBullseye,
                settings.BullseyeLatitudeDeg,
                settings.BullseyeLongitudeDeg,
                settings.TargetSymbolScale,
                _viewModel.IsAlwaysOnTop,
                _viewModel.ShowSettings,
                null,
                []);
        }

        return new WebSnapshot(
            true,
            "OK",
            _viewModel.ConnectionText,
            _viewModel.SourceText,
            _viewModel.TrafficText,
            _viewModel.RefreshRateText,
            settings.SelectedRangeNm,
            settings.OrientationMode.ToString(),
            settings.LabelMode.ToString(),
            settings.MapOpacity,
            MapboxDefaults.ResolveAccessToken(),
            MapboxDefaults.ResolveDisplayStyleUrl(settings.ShowControlledAirspaceLayer),
            string.Empty,
            settings.Declutter,
            settings.ShowMapLayer,
            settings.ShowAirspaceBoundaries,
            settings.ShowControlledAirspaceLayer,
            settings.TrailsEnabled,
            settings.ShowBullseye,
            settings.BullseyeLatitudeDeg,
            settings.BullseyeLongitudeDeg,
            settings.TargetSymbolScale,
            _viewModel.IsAlwaysOnTop,
            _viewModel.ShowSettings,
            new WebOwnship(
                picture.Ownship.Id,
                picture.Ownship.LatitudeDeg,
                picture.Ownship.LongitudeDeg,
                picture.Ownship.AltitudeFt,
                picture.Ownship.HeadingDeg,
                picture.Ownship.SpeedKt,
                picture.Ownship.Timestamp),
            picture.Targets.Select(static target => new WebTarget(
                target.Id,
                target.DisplayName,
                target.Category.ToString(),
                target.IsStale,
                target.RangeNm,
                target.BearingDegTrue,
                target.RelativeBearingDeg,
                target.RelativeAltitudeFt,
                target.HeadingDeg,
                target.SpeedKt,
                target.ClosureKt,
                target.Timestamp,
                target.History
                    .TakeLast(40)
                    .Select(static point => new WebHistoryPoint(
                        point.LatitudeDeg,
                        point.LongitudeDeg,
                        point.AltitudeFt,
                        point.Timestamp))
                    .ToArray()))
                .ToArray());
    }

    private CommandResult ExecuteCommand(string command)
    {
        if (_dispatcher.HasShutdownStarted || _dispatcher.HasShutdownFinished)
        {
            return new CommandResult(false, "Application is shutting down");
        }

        var stopwatch = Stopwatch.StartNew();
        try
        {
            var result = _dispatcher.CheckAccess()
                ? ExecuteCommandOnUi(command)
                : _dispatcher.InvokeAsync(() => ExecuteCommandOnUi(command), DispatcherPriority.Send)
                    .Task
                    .GetAwaiter()
                    .GetResult();

            stopwatch.Stop();
            if (result.Ok && stopwatch.Elapsed > CommandDispatchRefreshThreshold)
            {
                _viewModel.RequestCommandRefresh($"web-command:{command}", stopwatch.Elapsed);
            }

            return result;
        }
        catch (Exception ex)
        {
            DataSourceDebugLog.Info("Web", $"Command failed | command={command} error={ex}");
            return new CommandResult(false, ex.Message);
        }
    }

    private CommandResult ExecuteCommandOnUi(string command)
    {
        var normalized = command.Trim().ToLowerInvariant();
        switch (normalized)
        {
            case "range-up":
                _viewModel.IncreaseRangeCommand.Execute(null);
                break;
            case "range-down":
                _viewModel.DecreaseRangeCommand.Execute(null);
                break;
            case "orientation":
                _viewModel.ToggleOrientationCommand.Execute(null);
                break;
            case "map-opacity-up":
                _viewModel.IncreaseMapOpacityCommand.Execute(null);
                break;
            case "map-opacity-down":
                _viewModel.DecreaseMapOpacityCommand.Execute(null);
                break;
            case "overlay-opacity-up":
                _viewModel.IncreaseMapOverlayOpacityCommand.Execute(null);
                break;
            case "overlay-opacity-down":
                _viewModel.DecreaseMapOverlayOpacityCommand.Execute(null);
                break;
            case "map":
                _viewModel.ToggleMapCommand.Execute(null);
                break;
            case "declutter":
                _viewModel.ToggleDeclutterCommand.Execute(null);
                break;
            case "trails":
                _viewModel.ToggleTrailsCommand.Execute(null);
                break;
            case "bullseye":
                _viewModel.ToggleBullseyeCommand.Execute(null);
                break;
            case "labels":
                _viewModel.ToggleLabelsCommand.Execute(null);
                break;
            case "lara":
            case "airspace":
                _viewModel.ToggleAirspaceCommand.Execute(null);
                break;
            case "area":
                _viewModel.ToggleControlledAirspaceCommand.Execute(null);
                break;
            case "target-up":
                _viewModel.IncreaseTargetSymbolScaleCommand.Execute(null);
                break;
            case "target-down":
                _viewModel.DecreaseTargetSymbolScaleCommand.Execute(null);
                break;
            case "pin":
                _viewModel.ToggleAlwaysOnTopCommand.Execute(null);
                break;
            case "settings":
                _viewModel.ToggleSettingsCommand.Execute(null);
                break;
            default:
                return new CommandResult(false, $"Unknown command: {command}");
        }

        return new CommandResult(true, null);
    }

    private static IReadOnlyList<string> ResolveLocalUrls(int port)
    {
        var urls = new List<string> { $"http://localhost:{port.ToString(CultureInfo.InvariantCulture)}/" };
        try
        {
            var addresses = NetworkInterface.GetAllNetworkInterfaces()
                .Where(static adapter =>
                    adapter.OperationalStatus == OperationalStatus.Up &&
                    adapter.NetworkInterfaceType != NetworkInterfaceType.Loopback &&
                    adapter.NetworkInterfaceType != NetworkInterfaceType.Tunnel)
                .SelectMany(static adapter => adapter.GetIPProperties().UnicastAddresses)
                .Select(static address => address.Address)
                .Where(static address =>
                    address.AddressFamily == AddressFamily.InterNetwork &&
                    !IPAddress.IsLoopback(address))
                .Select(address => address.ToString())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(static address => address, StringComparer.Ordinal)
                .Select(address => $"http://{address}:{port.ToString(CultureInfo.InvariantCulture)}/");

            urls.AddRange(addresses);
        }
        catch
        {
        }

        return urls;
    }

    private sealed record WebSnapshot(
        bool Available,
        string Status,
        string Connection,
        string Source,
        string Traffic,
        string RefreshRate,
        int RangeNm,
        string Orientation,
        string LabelMode,
        double MapOpacity,
        string MapboxAccessToken,
        string MapboxStyleUrl,
        string MapboxAreasStyleUrl,
        bool Declutter,
        bool ShowMapLayer,
        bool ShowAirspaceBoundaries,
        bool ShowControlledAirspaceLayer,
        bool TrailsEnabled,
        bool ShowBullseye,
        double? BullseyeLatitudeDeg,
        double? BullseyeLongitudeDeg,
        double TargetSymbolScale,
        bool IsAlwaysOnTop,
        bool ShowSettings,
        WebOwnship? Ownship,
        IReadOnlyList<WebTarget> Targets)
    {
        public static WebSnapshot Unavailable(string status) =>
            new(
                false,
                status,
                "Disconnected",
                string.Empty,
                "0 contacts",
                "0.0 Hz",
                40,
                ScopeOrientationMode.HeadingUp.ToString(),
                global::TacticalDisplay.Core.Models.LabelMode.Minimal.ToString(),
                0.65,
                string.Empty,
                "mapbox://styles/mapbox/outdoors-v12",
                MapboxDefaults.FallbackAreasStyleUrl,
                false,
                true,
                false,
                true,
                true,
                false,
                null,
                null,
                1,
                false,
                false,
                null,
                []);
    }

    private sealed record WebOwnship(
        string Id,
        double LatitudeDeg,
        double LongitudeDeg,
        double AltitudeFt,
        double HeadingDeg,
        double? SpeedKt,
        DateTimeOffset Timestamp);

    private sealed record WebTarget(
        string Id,
        string DisplayName,
        string Category,
        bool IsStale,
        double RangeNm,
        double BearingDegTrue,
        double RelativeBearingDeg,
        double RelativeAltitudeFt,
        double? HeadingDeg,
        double? SpeedKt,
        double? ClosureKt,
        DateTimeOffset Timestamp,
        IReadOnlyList<WebHistoryPoint> History);

    private sealed record WebHistoryPoint(
        double LatitudeDeg,
        double LongitudeDeg,
        double AltitudeFt,
        DateTimeOffset Timestamp);

    private sealed record CommandResult(bool Ok, string? Error);

    private sealed record CommandResponse(bool Ok, string Error);

    private const string IndexHtml = """
<!doctype html>
<html lang="en">
<head>
  <meta charset="utf-8">
  <meta name="viewport" content="width=device-width, initial-scale=1, viewport-fit=cover">
  <title>VTSD Tablet Display</title>
  <link href="https://api.mapbox.com/mapbox-gl-js/v3.9.4/mapbox-gl.css" rel="stylesheet">
  <script src="https://api.mapbox.com/mapbox-gl-js/v3.9.4/mapbox-gl.js"></script>
  <style>
    :root { color-scheme: dark; }
    * { box-sizing: border-box; }
    html, body {
      width: 100%;
      height: 100%;
      margin: 0;
      overflow: hidden;
      background: #071015;
      color: #d9f2ec;
      font-family: Consolas, "SFMono-Regular", monospace;
    }
    body { padding: 4px; }
    .mfd-frame {
      width: 100%;
      height: 100%;
      display: grid;
      grid-template-columns: 68px minmax(0, 1fr) 92px;
      grid-template-rows: 54px minmax(0, 1fr) 78px;
      gap: 0;
      padding: 16px;
      border: 2px solid #3d5360;
      border-radius: 8px;
      background: transparent;
    }
    .top-controls, .bottom-controls, .fullscreen-controls {
      display: flex;
      align-items: center;
      justify-content: center;
    }
    .top-controls { grid-column: 2; grid-row: 1; }
    .fullscreen-controls {
      grid-column: 3;
      grid-row: 1;
      justify-content: flex-end;
    }
    .left-controls, .right-controls {
      display: flex;
      flex-direction: column;
      justify-content: center;
      align-items: stretch;
    }
    .left-controls { grid-column: 1; grid-row: 2; }
    .right-controls { grid-column: 3; grid-row: 2; }
    .display-surface {
      position: relative;
      grid-column: 2;
      grid-row: 2;
      min-width: 0;
      min-height: 0;
      overflow: hidden;
      border: 1px solid #355a56;
      border-radius: 2px;
      background: #071015;
    }
    .bottom {
      grid-column: 2;
      grid-row: 3;
      display: grid;
      grid-template-rows: 50px 1fr;
      min-width: 0;
    }
    .bottom-controls { grid-row: 1; align-items: flex-start; }
    .status {
      grid-row: 2;
      display: flex;
      gap: 16px;
      justify-content: center;
      align-items: flex-end;
      min-width: 0;
      padding: 0 4px 2px;
      color: #9afad7;
      font-size: 12px;
      }
    .status span {
      min-width: 0;
      overflow: hidden;
      text-overflow: ellipsis;
      white-space: nowrap;
    }
    .muted { color: #8ea5ad; }
    .warn { color: #f0c864; }
    #map {
      position: absolute;
      inset: 0;
      opacity: 0;
      transition: opacity 120ms linear;
    }
    #scope {
      position: absolute;
      inset: 0;
      width: 100%;
      height: 100%;
      display: block;
      touch-action: none;
      background: transparent;
    }
    button {
      height: 42px;
      min-height: 42px;
      min-width: 48px;
      margin: 4px 3px;
      padding: 4px 2px;
      border: 1px solid #617480;
      border-radius: 0;
      background: #283843;
      color: #d9f2ec;
      font: 600 11px Consolas, "SFMono-Regular", monospace;
      letter-spacing: 0;
      touch-action: manipulation;
    }
    .arrow-stack {
      display: flex;
      flex-direction: column;
      justify-content: center;
      margin: 0 10px;
    }
    .arrow-stack button {
      width: 22px;
      height: 18px;
      min-width: 22px;
      min-height: 18px;
      margin: 2px 1px;
      padding: 0;
      font-size: 10px;
    }
    .fullscreen-controls button {
      width: 42px;
      min-width: 42px;
      height: 22px;
      min-height: 22px;
      margin: 1px;
      font-size: 10px;
    }
    .tick {
      width: 5px;
      height: 34px;
      margin: 12px 8px;
      border: 1px solid #4c6570;
      background: #1a2a33;
    }
    .side-tick {
      width: 48px;
      height: 6px;
      margin: 4px auto;
      border: 1px solid #6f8792;
      background: #27414b;
    }
    .slot {
      height: 34px;
      min-width: 44px;
      margin: 5px;
      border: 1px solid #566976;
      background: #263742;
    }
    button.active {
      border-color: #9afad7;
      border-width: 2px;
      background: #294f48;
      color: #e9fff8;
    }
    button:active {
      transform: translateY(1px);
      background: #34505b;
    }
    .mapboxgl-control-container {
      font-family: Consolas, monospace;
      font-size: 10px;
    }
    @media (max-width: 720px) {
      body { padding: 2px; }
      .mfd-frame {
        grid-template-columns: 58px minmax(0, 1fr) 78px;
        grid-template-rows: 48px minmax(0, 1fr) 72px;
        padding: 8px;
      }
      button {
        min-width: 42px;
        height: 38px;
        min-height: 38px;
        margin: 3px 2px;
        font-size: 10px;
      }
      .tick { margin: 8px 4px; }
      .side-tick { width: 40px; }
      .fullscreen-controls button { width: 36px; min-width: 36px; }
      .status { gap: 8px; font-size: 10px; }
    }
  </style>
</head>
<body>
  <div class="mfd-frame">
    <div class="top-controls">
      <button type="button" data-command="orientation" id="orientationBtn">N/HDG</button>
      <div class="tick"></div>
      <div class="arrow-stack">
        <button type="button" data-command="map-opacity-up">↑</button>
        <button type="button" data-command="map-opacity-down">↓</button>
      </div>
      <div class="tick"></div>
      <button type="button" data-command="map" id="mapBtn">MAP</button>
    </div>
    <div class="fullscreen-controls">
      <button type="button" id="fullscreenBtn">FULL</button>
    </div>
    <div class="left-controls">
      <button type="button" data-command="range-up">RNG +</button>
      <div class="side-tick"></div>
      <button type="button" data-command="range-down">RNG -</button>
      <div class="side-tick"></div>
      <button type="button" data-command="declutter" id="declutterBtn">DCLR</button>
      <div class="side-tick"></div>
      <button type="button" data-command="trails" id="trailsBtn">TRAIL</button>
      <div class="side-tick"></div>
      <button type="button" data-command="bullseye" id="bullseyeBtn">BE</button>
    </div>
    <div class="right-controls">
      <button type="button" data-command="pin" id="pinBtn">PIN</button>
      <div class="side-tick"></div>
      <button type="button" data-command="lara" id="laraBtn">LARA</button>
      <div class="side-tick"></div>
      <button type="button" data-command="area" id="areaBtn">AREA</button>
      <div class="side-tick"></div>
      <div class="slot"></div>
      <div class="side-tick"></div>
      <button type="button" data-command="target-up">TGT +</button>
      <div class="side-tick"></div>
      <button type="button" data-command="target-down">TGT -</button>
    </div>
    <div class="display-surface">
      <div id="map"></div>
      <canvas id="scope"></canvas>
    </div>
    <div class="bottom">
      <div class="bottom-controls">
        <button type="button" data-command="labels" id="labelsBtn">LBL</button>
        <div class="tick"></div>
        <div class="arrow-stack">
          <button type="button" data-command="overlay-opacity-up">↑</button>
          <button type="button" data-command="overlay-opacity-down">↓</button>
        </div>
        <div class="tick"></div>
        <button type="button" data-command="settings" id="settingsBtn">SET</button>
      </div>
      <div class="status">
        <span id="range" class="muted">RANGE -- NM</span>
        <span id="connection">Disconnected</span>
        <span id="traffic">0 contacts</span>
        <span id="source" class="muted"></span>
        <span id="rate" class="muted">0.0 Hz</span>
        <span id="message" class="warn"></span>
      </div>
    </div>
  </div>
  <script>
    const canvas = document.getElementById('scope');
    const ctx = canvas.getContext('2d');
    const mapElement = document.getElementById('map');
    const metersPerNauticalMile = 1852.0;
    const webMercatorMetersPerPixelAtEquator = 78271.516964;
    let map = null;
    let mapReady = false;
    let currentMapboxToken = '';
    let currentMapboxStyle = '';
    let currentAreasStyle = '';
    let areasOverlayPromise = null;
    let areasLayerIds = [];
    let areasSourceIds = [];
    const fields = {
      range: document.getElementById('range'),
      source: document.getElementById('source'),
      connection: document.getElementById('connection'),
      traffic: document.getElementById('traffic'),
      rate: document.getElementById('rate'),
      message: document.getElementById('message'),
      orientationBtn: document.getElementById('orientationBtn'),
      mapBtn: document.getElementById('mapBtn'),
      declutterBtn: document.getElementById('declutterBtn'),
      trailsBtn: document.getElementById('trailsBtn'),
      bullseyeBtn: document.getElementById('bullseyeBtn'),
      labelsBtn: document.getElementById('labelsBtn'),
      laraBtn: document.getElementById('laraBtn'),
      areaBtn: document.getElementById('areaBtn'),
      pinBtn: document.getElementById('pinBtn'),
      settingsBtn: document.getElementById('settingsBtn')
    };
    const fullscreenBtn = document.getElementById('fullscreenBtn');
    let snapshot = null;
    let lastOkAt = 0;

    function resize() {
      const rect = canvas.getBoundingClientRect();
      const dpr = Math.max(window.devicePixelRatio || 1, 1);
      const width = Math.max(1, Math.floor(rect.width * dpr));
      const height = Math.max(1, Math.floor(rect.height * dpr));
      if (canvas.width !== width || canvas.height !== height) {
        canvas.width = width;
        canvas.height = height;
      }
      ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
      draw();
    }

    async function poll() {
      try {
        const response = await fetch('/api/snapshot', { cache: 'no-store' });
        if (!response.ok) throw new Error(response.status + ' ' + response.statusText);
        snapshot = await response.json();
        lastOkAt = Date.now();
        updateText();
        draw();
      } catch (error) {
        fields.message.textContent = 'Connection lost';
      }
    }

    function updateText() {
      if (!snapshot) return;
      fields.range.textContent = `${snapshot.orientation === 'NorthUp' ? 'N-UP' : 'HDG-UP'} | RANGE ${snapshot.rangeNm} NM`;
      fields.source.textContent = snapshot.source || '';
      fields.connection.textContent = snapshot.connection || 'Disconnected';
      fields.traffic.textContent = snapshot.traffic || '0 contacts';
      fields.rate.textContent = snapshot.refreshRate || '0.0 Hz';
      fields.message.textContent = snapshot.available ? '' : snapshot.status;
      fields.orientationBtn.classList.toggle('active', snapshot.orientation === 'HeadingUp');
      fields.orientationBtn.textContent = snapshot.orientation === 'HeadingUp' ? 'HDG-UP' : 'N-UP';
      setToggle(fields.mapBtn, snapshot.showMapLayer, 'MAP');
      setToggle(fields.declutterBtn, snapshot.declutter, 'DCLR');
      setToggle(fields.trailsBtn, snapshot.trailsEnabled, 'TRAIL');
      setToggle(fields.bullseyeBtn, snapshot.showBullseye, 'BE');
      setToggle(fields.labelsBtn, snapshot.labelMode !== 'Off', `LBL ${snapshot.labelMode || ''}`.trim());
      setToggle(fields.laraBtn, snapshot.showAirspaceBoundaries, 'LARA');
      setToggle(fields.areaBtn, snapshot.showControlledAirspaceLayer, 'AREA');
      setToggle(fields.pinBtn, snapshot.isAlwaysOnTop, 'PIN');
      setToggle(fields.settingsBtn, snapshot.showSettings, 'SET');
      updateMap();
    }

    function setToggle(button, isActive, text) {
      button.classList.toggle('active', !!isActive);
      button.textContent = text;
    }

    async function toggleFullscreen() {
      try {
        if (document.fullscreenElement) {
          await document.exitFullscreen();
        } else {
          const proceed = window.confirm('This application is not intended to be used in fullscreen mode.\n\nContinue anyway?');
          if (!proceed) return;
          await document.documentElement.requestFullscreen();
        }
      } catch {
        fields.message.textContent = 'Fullscreen unavailable';
      }
    }

    function updateFullscreenButton() {
      fullscreenBtn.classList.toggle('active', !!document.fullscreenElement);
      fullscreenBtn.textContent = document.fullscreenElement ? 'EXIT' : 'FULL';
    }

    async function sendCommand(command) {
      fields.message.textContent = '';
      try {
        const response = await fetch('/api/command', {
          method: 'POST',
          headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify({ command })
        });
        if (!response.ok) {
          const text = await response.text();
          throw new Error(text || response.statusText);
        }
        await poll();
      } catch (error) {
        fields.message.textContent = 'Command failed';
      }
    }

    function draw() {
      const w = canvas.clientWidth;
      const h = canvas.clientHeight;
      ctx.clearRect(0, 0, w, h);
      const mapVisible = snapshot?.available && snapshot?.ownship && snapshot?.showMapLayer && mapReady;
      if (!mapVisible) {
        const gradient = ctx.createLinearGradient(0, 0, w, h);
        gradient.addColorStop(0, '#061017');
        gradient.addColorStop(1, '#0a2021');
        ctx.fillStyle = gradient;
        ctx.fillRect(0, 0, w, h);
      }

      const center = { x: w / 2, y: h / 2 };
      const radius = Math.max(40, Math.min(w, h) * 0.44);
      drawRings(center, radius);
      drawOwnship(center, radius);

      if (!snapshot || !snapshot.available || !snapshot.ownship) {
        drawCenteredText(snapshot?.status || 'Waiting for data', center.x, center.y + 38, '#f0c864', 15);
        return;
      }

      if (snapshot.trailsEnabled) {
        for (const target of snapshot.targets) drawTrail(target, center, radius);
      }
      for (const target of snapshot.targets) drawTarget(target, center, radius);
    }

    function drawRings(center, radius) {
      ctx.save();
      ctx.strokeStyle = 'rgba(140,255,220,0.78)';
      ctx.lineWidth = 1.1;
      ctx.font = '600 11px Consolas, monospace';
      ctx.fillStyle = '#afffde';
      for (let i = 1; i <= 4; i++) {
        const ring = radius * i / 4;
        ctx.beginPath();
        ctx.arc(center.x, center.y, ring, 0, Math.PI * 2);
        ctx.stroke();
        ctx.fillText(`${Math.round((snapshot?.rangeNm || 40) * i / 4)} NM`, center.x + 6, center.y - ring - 8);
      }
      const headings = [['360',0], ['045',45], ['090',90], ['135',135], ['180',180], ['225',225], ['270',270], ['315',315]];
      for (const [label, bearing] of headings) {
        const display = snapshot?.orientation === 'HeadingUp' && snapshot?.ownship
          ? normalize(bearing - snapshot.ownship.headingDeg)
          : bearing;
        const rad = display * Math.PI / 180;
        const x = center.x + (radius + 15) * Math.sin(rad);
        const y = center.y - (radius + 15) * Math.cos(rad);
        drawCenteredText(label, x, y, '#afffde', 13, '700');
      }
      ctx.restore();
    }

    function drawOwnship(center, radius) {
      const heading = snapshot?.orientation === 'HeadingUp' || !snapshot?.ownship ? 0 : snapshot.ownship.headingDeg;
      ctx.save();
      ctx.translate(center.x, center.y);
      ctx.rotate(heading * Math.PI / 180);
      ctx.strokeStyle = '#aaffdc';
      ctx.fillStyle = '#aaffdc';
      ctx.lineWidth = 1.5;
      ctx.beginPath();
      ctx.moveTo(0, -10);
      ctx.lineTo(-8, 8);
      ctx.lineTo(8, 8);
      ctx.closePath();
      ctx.fill();
      ctx.strokeStyle = '#001014';
      ctx.stroke();
      ctx.strokeStyle = '#aaffdc';
      ctx.beginPath();
      ctx.moveTo(0, 0);
      ctx.lineTo(0, -radius);
      ctx.stroke();
      ctx.restore();
    }

    function drawTrail(target, center, radius) {
      if (!snapshot?.ownship || !target.history || target.history.length < 2) return;
      ctx.save();
      ctx.strokeStyle = 'rgba(140,230,220,0.36)';
      ctx.lineWidth = 1;
      ctx.beginPath();
      let started = false;
      for (const point of target.history) {
        const p = projectGeo(point.latitudeDeg, point.longitudeDeg, center, radius, false);
        if (!started) {
          ctx.moveTo(p.x, p.y);
          started = true;
        } else {
          ctx.lineTo(p.x, p.y);
        }
      }
      ctx.stroke();
      ctx.restore();
    }

    function drawTarget(target, center, radius) {
      const p = projectPolar(target.rangeNm, target.bearingDegTrue, center, radius);
      const color = colorFor(target.category);
      const scale = Math.min(Math.max(snapshot.targetSymbolScale || 1, 0.6), 1.8);
      ctx.save();
      ctx.globalAlpha = target.isStale ? 0.45 : 1;
      ctx.strokeStyle = color;
      ctx.fillStyle = color;
      ctx.lineWidth = 1.5 * scale;
      drawTargetHeading(target, p, scale);
      if (target.category === 'Friend') {
        circle(p.x, p.y, 5 * scale, false);
      } else if (target.category === 'Enemy') {
        ctx.beginPath();
        ctx.moveTo(p.x - 5 * scale, p.y - 5 * scale);
        ctx.lineTo(p.x + 5 * scale, p.y + 5 * scale);
        ctx.moveTo(p.x - 5 * scale, p.y + 5 * scale);
        ctx.lineTo(p.x + 5 * scale, p.y - 5 * scale);
        ctx.stroke();
      } else if (target.category === 'Package') {
        ctx.beginPath();
        ctx.moveTo(p.x, p.y - 6 * scale);
        ctx.lineTo(p.x + 6 * scale, p.y);
        ctx.lineTo(p.x, p.y + 6 * scale);
        ctx.lineTo(p.x - 6 * scale, p.y);
        ctx.closePath();
        ctx.stroke();
      } else if (target.category === 'Support') {
        ctx.strokeRect(p.x - 4 * scale, p.y - 4 * scale, 8 * scale, 8 * scale);
      } else {
        circle(p.x, p.y, 2.5 * scale, true);
      }
      if (snapshot.labelMode !== 'Off') drawLabel(target, p);
      ctx.restore();
    }

    function drawTargetHeading(target, p, scale) {
      if (target.headingDeg == null) return;
      const heading = snapshot.orientation === 'HeadingUp'
        ? normalize(target.headingDeg - snapshot.ownship.headingDeg)
        : normalize(target.headingDeg);
      const rad = heading * Math.PI / 180;
      ctx.beginPath();
      ctx.moveTo(p.x, p.y);
      ctx.lineTo(p.x + 12 * scale * Math.sin(rad), p.y - 12 * scale * Math.cos(rad));
      ctx.stroke();
    }

    function drawLabel(target, p) {
      const rel = target.relativeAltitudeFt >= 0 ? `+${Math.round(target.relativeAltitudeFt / 100)}` : `${Math.round(target.relativeAltitudeFt / 100)}`;
      const primary = `${target.displayName} ${target.rangeNm.toFixed(1)} ${rel}${target.isStale ? ' STALE' : ''}`;
      const lines = [primary];
      if (snapshot.labelMode === 'Full' && !target.isStale) {
        const heading = target.headingDeg == null ? '---' : `${Math.round(target.headingDeg).toString().padStart(3, '0')}`;
        const closure = target.closureKt == null ? '---' : `${Math.round(target.closureKt)}`;
        lines.push(`${heading} ${closure}`);
      }
      ctx.font = '14px Consolas, monospace';
      const width = Math.max(...lines.map(line => ctx.measureText(line).width)) + 8;
      const height = lines.length * 16 + 4;
      let x = p.x + 10;
      let y = p.y - 16;
      if (x + width > canvas.clientWidth) x = p.x - width - 10;
      if (y + height > canvas.clientHeight) y = canvas.clientHeight - height - 4;
      if (y < 4) y = 4;
      ctx.globalAlpha = 1;
      ctx.fillStyle = 'rgba(3,10,16,0.72)';
      ctx.strokeStyle = 'rgba(110,180,170,0.45)';
      ctx.lineWidth = 1;
      ctx.fillRect(x, y, width, height);
      ctx.strokeRect(x, y, width, height);
      ctx.fillStyle = '#bfffea';
      lines.forEach((line, index) => ctx.fillText(line, x + 4, y + 15 + index * 16));
      ctx.strokeStyle = 'rgba(120,210,200,0.45)';
      ctx.beginPath();
      ctx.moveTo(p.x, p.y);
      ctx.lineTo(Math.max(x, Math.min(p.x, x + width)), Math.max(y, Math.min(p.y, y + height)));
      ctx.stroke();
    }

    function projectPolar(rangeNm, bearingDeg, center, radius) {
      const used = snapshot.orientation === 'HeadingUp'
        ? normalize(bearingDeg - snapshot.ownship.headingDeg)
        : normalize(bearingDeg);
      const ratio = Math.min(rangeNm / Math.max(snapshot.rangeNm || 1, 1), 1);
      const rad = used * Math.PI / 180;
      return { x: center.x + ratio * radius * Math.sin(rad), y: center.y - ratio * radius * Math.cos(rad) };
    }

    function initMap() {
      if (!window.mapboxgl) {
        fields.message.textContent = 'Map unavailable';
        return;
      }

      const token = (snapshot?.mapboxAccessToken || '').trim();
      const style = (snapshot?.mapboxStyleUrl || '').trim() || 'mapbox://styles/mapbox/outdoors-v12';
      if (!token) {
        if (map) {
          map.remove();
          map = null;
          mapReady = false;
          currentAreasStyle = '';
          areasLayerIds = [];
          areasSourceIds = [];
        }
        fields.message.textContent = 'Map unavailable';
        mapElement.style.opacity = '0';
        return;
      }

      if (map && token === currentMapboxToken && style === currentMapboxStyle) {
        return;
      }

      if (map) {
        map.remove();
        map = null;
        mapReady = false;
        currentAreasStyle = '';
        areasLayerIds = [];
        areasSourceIds = [];
      }

      currentMapboxToken = token;
      currentMapboxStyle = style;
      mapboxgl.accessToken = token;
      map = new mapboxgl.Map({
        container: 'map',
        style,
        center: snapshot?.ownship ? [snapshot.ownship.longitudeDeg, snapshot.ownship.latitudeDeg] : [24.9633, 60.3172],
        zoom: 7,
        bearing: 0,
        pitch: 0,
        attributionControl: true,
        interactive: false
      });
      map.dragPan.disable();
      map.scrollZoom.disable();
      map.boxZoom.disable();
      map.dragRotate.disable();
      map.keyboard.disable();
      map.doubleClickZoom.disable();
      map.touchZoomRotate.disable();
      map.on('load', () => {
        mapReady = true;
        fields.message.textContent = '';
        updateAreasOverlay();
        updateMap();
        draw();
      });
      map.on('error', () => {
        fields.message.textContent = 'Map unavailable';
      });
    }

    function areasStyleApiUrl(styleUrl, token) {
      const trimmed = (styleUrl || '').trim();
      if (/^https?:\/\//i.test(trimmed)) {
        const separator = trimmed.includes('?') ? '&' : '?';
        return trimmed.includes('access_token=') ? trimmed : `${trimmed}${separator}access_token=${encodeURIComponent(token)}`;
      }
      const match = /^mapbox:\/\/styles\/([^/]+)\/([^/?#]+)/i.exec(trimmed);
      if (!match) return '';
      return `https://api.mapbox.com/styles/v1/${encodeURIComponent(match[1])}/${encodeURIComponent(match[2])}?access_token=${encodeURIComponent(token)}`;
    }

    function removeAreasOverlay() {
      if (!map) return;
      for (const id of [...areasLayerIds].reverse()) {
        if (map.getLayer(id)) map.removeLayer(id);
      }
      for (const id of [...areasSourceIds].reverse()) {
        if (map.getSource(id)) map.removeSource(id);
      }
      areasLayerIds = [];
      areasSourceIds = [];
      currentAreasStyle = '';
    }

    function setAreasVisibility(visible) {
      if (!map) return;
      const visibility = visible ? 'visible' : 'none';
      for (const id of areasLayerIds) {
        if (map.getLayer(id)) map.setLayoutProperty(id, 'visibility', visibility);
      }
    }

    async function addAreasOverlay() {
      if (!snapshot || !map || !map.isStyleLoaded()) return;
      const styleUrl = (snapshot.mapboxAreasStyleUrl || '').trim();
      const token = (snapshot.mapboxAccessToken || '').trim();
      if (!styleUrl || !token) {
        removeAreasOverlay();
        return;
      }

      if (currentAreasStyle === styleUrl && areasLayerIds.length > 0) {
        setAreasVisibility(!!snapshot.showControlledAirspaceLayer);
        return;
      }

      const apiUrl = areasStyleApiUrl(styleUrl, token);
      if (!apiUrl) {
        removeAreasOverlay();
        return;
      }

      removeAreasOverlay();
      const response = await fetch(apiUrl, { cache: 'force-cache' });
      if (!response.ok) throw new Error(`Areas style ${response.status}`);
      const style = await response.json();
      const sourceMap = new globalThis.Map();
      for (const [sourceId, source] of Object.entries(style.sources || {})) {
        const prefixedSourceId = `areas-${sourceId}`;
        if (!map.getSource(prefixedSourceId)) {
          map.addSource(prefixedSourceId, JSON.parse(JSON.stringify(source)));
          areasSourceIds.push(prefixedSourceId);
        }
        sourceMap.set(sourceId, prefixedSourceId);
      }

      for (const layer of style.layers || []) {
        if (!layer || layer.type === 'background') continue;
        const copy = JSON.parse(JSON.stringify(layer));
        copy.id = `areas-${copy.id}`;
        if (copy.source && sourceMap.has(copy.source)) {
          copy.source = sourceMap.get(copy.source);
        } else if (copy.source) {
          continue;
        }
        copy.layout = copy.layout || {};
        copy.layout.visibility = snapshot.showControlledAirspaceLayer ? 'visible' : 'none';
        if (!map.getLayer(copy.id)) {
          map.addLayer(copy);
          areasLayerIds.push(copy.id);
        }
      }

      currentAreasStyle = styleUrl;
    }

    function updateAreasOverlay() {
      if (!snapshot || !map || !map.isStyleLoaded()) return;
      if (areasOverlayPromise) return;
      areasOverlayPromise = addAreasOverlay()
        .catch(() => { fields.message.textContent = 'Areas unavailable'; })
        .finally(() => { areasOverlayPromise = null; });
    }

    function updateMap() {
      initMap();
      const show = snapshot?.available && snapshot?.ownship && snapshot?.showMapLayer && mapReady;
      mapElement.style.opacity = show ? Math.max(0, Math.min(snapshot.mapOpacity ?? 0.65, 1)).toString() : '0';
      if (!show || !map) return;

      map.resize();
      map.jumpTo({
        center: [snapshot.ownship.longitudeDeg, snapshot.ownship.latitudeDeg],
        zoom: calculateZoom(),
        bearing: snapshot.orientation === 'HeadingUp' ? snapshot.ownship.headingDeg : 0,
        pitch: 0
      });
      updateAreasOverlay();
    }

    function calculateZoom() {
      const rect = mapElement.getBoundingClientRect();
      const radiusPixels = Math.max(1, Math.min(rect.width, rect.height) * 0.45);
      const rangeMeters = Math.max(1, snapshot.rangeNm) * metersPerNauticalMile;
      const metersPerPixel = rangeMeters / radiusPixels;
      const latitudeScale = Math.max(Math.cos(snapshot.ownship.latitudeDeg * Math.PI / 180.0), 0.05);
      const zoom = Math.log2(webMercatorMetersPerPixelAtEquator * latitudeScale / metersPerPixel);
      return Math.max(0, Math.min(18, zoom));
    }

    function projectGeo(lat, lon, center, radius, clamp) {
      const own = snapshot.ownship;
      const metersPerNm = 1852;
      const earth = 6378137;
      const ownMerc = merc(own.latitudeDeg, own.longitudeDeg, earth);
      const tgtMerc = merc(lat, lon, earth);
      const latScale = Math.max(Math.cos(own.latitudeDeg * Math.PI / 180), 0.05);
      let east = (tgtMerc.x - ownMerc.x) * radius * latScale / (Math.max(snapshot.rangeNm, 1) * metersPerNm);
      let north = (tgtMerc.y - ownMerc.y) * radius * latScale / (Math.max(snapshot.rangeNm, 1) * metersPerNm);
      if (snapshot.orientation === 'HeadingUp') {
        const rad = normalize(own.headingDeg) * Math.PI / 180;
        const rotatedEast = east * Math.cos(rad) - north * Math.sin(rad);
        const rotatedNorth = east * Math.sin(rad) + north * Math.cos(rad);
        east = rotatedEast;
        north = rotatedNorth;
      }
      if (clamp) {
        const length = Math.hypot(east, north);
        if (length > radius && length > 0) {
          east *= radius / length;
          north *= radius / length;
        }
      }
      return { x: center.x + east, y: center.y - north };
    }

    function merc(lat, lon, earth) {
      const clamped = Math.max(-85.05112878, Math.min(85.05112878, lat));
      const latRad = clamped * Math.PI / 180;
      return {
        x: earth * lon * Math.PI / 180,
        y: earth * Math.log(Math.tan(Math.PI / 4 + latRad / 2))
      };
    }

    function colorFor(category) {
      if (category === 'Friend') return '#78dcff';
      if (category === 'Enemy') return '#ff5f5f';
      if (category === 'Package') return '#50f0aa';
      if (category === 'Support') return '#f0c864';
      return '#ff6464';
    }

    function circle(x, y, radius, fill) {
      ctx.beginPath();
      ctx.arc(x, y, radius, 0, Math.PI * 2);
      fill ? ctx.fill() : ctx.stroke();
    }

    function drawCenteredText(text, x, y, color, size, weight = '400') {
      ctx.save();
      ctx.font = `${weight} ${size}px Consolas, monospace`;
      ctx.fillStyle = color;
      ctx.textAlign = 'center';
      ctx.textBaseline = 'middle';
      ctx.fillText(text, x, y);
      ctx.restore();
    }

    function normalize(deg) {
      return ((deg % 360) + 360) % 360;
    }

    window.addEventListener('resize', resize);
    fullscreenBtn.addEventListener('click', toggleFullscreen);
    document.addEventListener('fullscreenchange', updateFullscreenButton);
    for (const button of document.querySelectorAll('[data-command]')) {
      button.addEventListener('click', () => sendCommand(button.dataset.command));
    }
    setInterval(poll, 500);
    setInterval(() => {
      if (lastOkAt && Date.now() - lastOkAt > 2500) fields.message.textContent = 'Connection stale';
    }, 1000);
    initMap();
    updateFullscreenButton();
    resize();
    poll();
  </script>
</body>
</html>
""";
}

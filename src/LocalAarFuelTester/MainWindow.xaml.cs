using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using TacticalDisplay.App.Data;

namespace VTSD.LocalAarFuelTester;

public partial class MainWindow : Window
{
    private readonly DispatcherTimer _dispatchTimer;
    private SimConnectCommBusClient? _client;
    private CancellationTokenSource? _transferCts;
    private AarBridgeFuelState? _lastFuelState;
    private bool _fuelRead;
    private bool _fuelWrite;
    private bool _hasFuelState;
    private string[] _writeMethods = [];

    public MainWindow()
    {
        InitializeComponent();
        SimConnectPathBox.Text = Environment.GetEnvironmentVariable("MSFS_SIMCONNECT_DLL") ??
            Path.Combine(AppContext.BaseDirectory, "SimConnect.dll");
        _dispatchTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(25) };
        _dispatchTimer.Tick += (_, _) => _client?.Dispatch();
        _dispatchTimer.Start();
        Append("This local-only utility is not part of the VTSD release.");
    }

    private async void OnConnectClick(object sender, RoutedEventArgs e)
    {
        SetBusy(true, "Connecting to MSFS SimConnect…");
        try
        {
            _client?.Dispose();
            _client = new SimConnectCommBusClient(SimConnectPathBox.Text.Trim());
            _client.Connect();
            var hello = await SendAsync("HELLO");
            if (!string.Equals(hello.Status, "Success", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(hello.Error ?? "The bridge did not accept HELLO.");

            var capabilities = await SendAsync("GET_CAPABILITIES");
            _fuelRead = capabilities.Capabilities.Contains("fuel.read", StringComparer.Ordinal);
            _fuelWrite = capabilities.Capabilities.Contains("fuel.write", StringComparer.Ordinal);
            _writeMethods = capabilities.Diagnostics?.WriteMethods ?? [];
            if (!_fuelRead)
            {
                _hasFuelState = false;
                StatusText.Text = $"Bridge {capabilities.BridgeVersion ?? "unknown"} connected; fuel.read is unavailable. {capabilities.Diagnostics?.FuelReadDiagnostic}";
                Append(StatusText.Text);
                UpdateButtons();
                return;
            }

            var state = await SendAsync("GET_FUEL_STATE");
            ShowFuelState(state);
            StatusText.Text = $"Bridge {capabilities.BridgeVersion ?? "unknown"} · read={_fuelRead} · write={_fuelWrite}\n" +
                $"Current {state.FuelState?.CurrentFuelKg:0.00} kg / {state.FuelState?.CapacityKg:0.00} kg · tanks={state.FuelState?.Tanks.Length ?? 0}";
            Append($"Connected: bridge={capabilities.BridgeVersion ?? "unknown"}, fuel.read={_fuelRead}, fuel.write={_fuelWrite}");
            Append($"Fuel write method(s): {(_writeMethods.Length == 0 ? "auto" : string.Join(", ", _writeMethods))}");
            Append(capabilities.Diagnostics?.FuelReadOnlyReason is { Length: > 0 } reason ? $"Probe: {reason}" : "Write probe: capability advertised by bridge.");
            UpdateButtons();
        }
        catch (Exception ex)
        {
            _fuelRead = _fuelWrite = _hasFuelState = false;
            StatusText.Text = ex.Message;
            Append("ERROR: " + ex.Message);
            UpdateButtons();
        }
        finally { SetBusy(false, StatusText.Text); }
    }

    private async void OnAddClick(object sender, RoutedEventArgs e) => await ApplyDeltaAsync(1);
    private async void OnRemoveClick(object sender, RoutedEventArgs e) => await ApplyDeltaAsync(-1);
    private void OnStopClick(object sender, RoutedEventArgs e) => _transferCts?.Cancel();

    private async Task ApplyDeltaAsync(int sign)
    {
        if (!double.TryParse(AmountBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var amount) ||
            !double.IsFinite(amount) || amount is < 0.1 or > 5000)
        {
            MessageBox.Show(this, "Enter a fuel amount from 0.1 to 5000 kg.", "Invalid amount", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (!double.TryParse(DurationBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var durationSeconds) ||
            !double.IsFinite(durationSeconds) || durationSeconds is < 1 or > 600)
        {
            MessageBox.Show(this, "Enter a transfer duration from 1 to 600 seconds.", "Invalid duration", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var requestedKg = sign * amount;
        if (sign > 0 && _lastFuelState is { } current && amount > current.CapacityKg - current.CurrentFuelKg + 0.05)
        {
            MessageBox.Show(this,
                $"This aircraft has room for at most {Math.Max(0, current.CapacityKg - current.CurrentFuelKg):0.##} kg.",
                "Amount exceeds available capacity", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var transferInterval = TimeSpan.FromSeconds(durationSeconds);
        var chunkCount = Math.Clamp((int)Math.Ceiling(durationSeconds / 2.0), 1, 30);
        var requestedChunkKg = amount / chunkCount;
        var action = sign > 0 ? "add to" : "remove from";
        if (MessageBox.Show(this,
                $"This will {action} up to {amount:0.##} kg over {durationSeconds:0} seconds in {chunkCount} steps of about {requestedChunkKg:0.##} kg.\n\n" +
                "The bridge verifies each step. The transfer stops on the first failed or partial write. Continue?",
                "Change simulator fuel", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;

        SetBusy(true, sign > 0 ? "Adding fuel…" : "Removing fuel…");
        TransferProgress.Visibility = Visibility.Visible;
        TransferProgress.Value = 0;
        AddButton.IsEnabled = RemoveButton.IsEnabled = false;
        using var transferCts = new CancellationTokenSource();
        _transferCts = transferCts;
        var appliedTotalKg = 0.0;
        var startedAt = DateTimeOffset.UtcNow;
        try
        {
            for (var index = 0; index < chunkCount; index++)
            {
                transferCts.Token.ThrowIfCancellationRequested();
                var elapsedSinceStart = DateTimeOffset.UtcNow - startedAt;
                var targetElapsed = TimeSpan.FromTicks(transferInterval.Ticks * index / chunkCount);
                if (targetElapsed > elapsedSinceStart)
                    await Task.Delay(targetElapsed - elapsedSinceStart, transferCts.Token);

                var remainingKg = amount - Math.Abs(appliedTotalKg);
                if (remainingKg < 0.001) break;
                var stepKg = Math.Min(requestedChunkKg, remainingKg);
                // Once a write is sent, wait for its read-back before honoring STOP so we never
                // lose track of a fuel mutation that the simulator may already have applied.
                var response = await ApplyAndVerifyFuelDeltaAsync(sign * stepKg);
                var appliedKg = response.AppliedKg ?? 0;
                appliedTotalKg += appliedKg;
                TransferProgress.Value = 100.0 * Math.Abs(appliedTotalKg) / amount;
                Append($"Step {index + 1}/{chunkCount}: requested {sign * stepKg:+0.##;-0.##;0} kg, applied {appliedKg:+0.##;-0.##;0} kg, status={response.Status ?? "unknown"}");
                if (response.FuelState is not null) ShowFuelState(response, logTanks: false);
                StatusText.Text = $"{(sign > 0 ? "Adding" : "Removing")} fuel · step {index + 1}/{chunkCount}\n" +
                    $"Applied {appliedTotalKg:+0.##;-0.##;0} kg of {requestedKg:+0.##;-0.##;0} kg";

                var complete = string.Equals(response.Status, "Success", StringComparison.OrdinalIgnoreCase) &&
                    Math.Abs(appliedKg - sign * stepKg) <= 0.05;
                if (!complete)
                {
                    if (response.FuelState is null)
                    {
                        try { await RefreshFuelStateAsync(); }
                        catch (Exception refreshError) { Append($"Could not recheck fuel after stopped step: {refreshError.Message}"); }
                    }
                    Append($"Transfer stopped after step {index + 1}: {response.Error ?? "bridge did not verify the full step"}");
                    break;
                }
                transferCts.Token.ThrowIfCancellationRequested();
            }
            var elapsed = DateTimeOffset.UtcNow - startedAt;
            Append($"Transfer finished: requested {requestedKg:+0.##;-0.##;0} kg, applied {appliedTotalKg:+0.##;-0.##;0} kg in {(int)elapsed.TotalSeconds}s.");
            StatusText.Text = $"Transfer finished · {appliedTotalKg:+0.##;-0.##;0} kg / {requestedKg:+0.##;-0.##;0} kg";
        }
        catch (OperationCanceledException) when (transferCts.IsCancellationRequested)
        {
            Append($"TRANSFER CANCELLED: applied so far {appliedTotalKg:+0.##;-0.##;0} kg of {requestedKg:+0.##;-0.##;0} kg.");
            StatusText.Text = $"Cancelled · applied {appliedTotalKg:+0.##;-0.##;0} kg";
        }
        catch (Exception ex)
        {
            Append($"TRANSFER STOPPED: applied so far {appliedTotalKg:+0.##;-0.##;0} kg of {requestedKg:+0.##;-0.##;0} kg · {ex.Message}");
            StatusText.Text = $"Transfer stopped · applied {appliedTotalKg:+0.##;-0.##;0} kg · {ex.Message}";
        }
        finally
        {
            _transferCts = null;
            TransferProgress.Visibility = Visibility.Collapsed;
            SetBusy(false, StatusText.Text);
            UpdateButtons();
        }
    }

    private async Task RefreshFuelStateAsync()
    {
        var response = await SendAsync("GET_FUEL_STATE");
        ShowFuelState(response);
    }

    private async Task<AarBridgeResponse> ApplyAndVerifyFuelDeltaAsync(double deltaKg)
    {
        var baselineResponse = await SendAsync("GET_FUEL_STATE");
        var baseline = baselineResponse.FuelState ?? throw new InvalidDataException("The bridge returned no pre-transfer fuel state.");
        var method = _writeMethods.FirstOrDefault() ?? "auto";
        Append($"Applying {deltaKg:+0.##;-0.##;0} kg with {method}.");
        var apply = await SendAsync("APPLY_FUEL_DELTA", deltaKg, allowFailedResponse: true, writeMethod: method);
        if (string.Equals(apply.Status, "Failed", StringComparison.OrdinalIgnoreCase)) return apply;

        var native = string.Equals(method, "a32nx-native", StringComparison.Ordinal);
        AarBridgeFuelState? previous = null;
        AarBridgeFuelState? verified = null;
        var latest = apply.FuelState;
        try
        {
            // Headwind/FBW owns its tank state. Keep its refuel controller active until
            // simulator read-back proves the change settled, rather than trusting a transient write.
            for (var sample = 0; sample < (native ? 30 : 6); sample++)
            {
                await Task.Delay(native ? 500 : sample == 0 ? 450 : 650);
                var stateResponse = await SendAsync("GET_FUEL_STATE");
                if (stateResponse.FuelState is not { } current) continue;
                latest = current;
                var appliedKg = current.CurrentFuelKg - baseline.CurrentFuelKg;
                var directionMatches = Math.Abs(appliedKg) >= 1 && Math.Sign(appliedKg) == Math.Sign(deltaKg);
                var withinRequest = Math.Abs(appliedKg) <= Math.Abs(deltaKg) + 5;
                var stable = previous is not null && Math.Abs(current.CurrentFuelKg - previous.CurrentFuelKg) <= 3;
                Append($"Read-back {sample + 1}: {current.CurrentFuelKg:0.00} kg; applied {appliedKg:+0.##;-0.##;0} kg; stable={stable}.");
                previous = current;
                if (directionMatches && withinRequest && stable)
                {
                    verified = current;
                    break;
                }
            }
        }
        finally
        {
            if (native)
            {
                try { await SendAsync("FINISH_FUEL_OPERATION", allowFailedResponse: true); }
                catch (Exception ex) { Append($"Could not finish aircraft refuel controller cleanly: {ex.Message}"); }
            }
        }

        if (verified is null)
        {
            var actual = latest is null ? 0 : latest.CurrentFuelKg - baseline.CurrentFuelKg;
            var status = Math.Abs(actual) >= 1 && Math.Sign(actual) == Math.Sign(deltaKg) ? "Partial" : "Failed";
            return apply with
            {
                Status = status,
                Error = "The simulator did not retain a stable fuel change; no more transfer was attempted.",
                AppliedKg = status == "Partial" ? actual : 0,
                FuelState = latest
            };
        }

        var stableDelta = verified.CurrentFuelKg - baseline.CurrentFuelKg;
        return apply with
        {
            Status = Math.Abs(stableDelta - deltaKg) <= 5 ? "Success" : "Partial",
            Error = Math.Abs(stableDelta - deltaKg) <= 5 ? null : "The simulator retained a stable partial fuel change.",
            AppliedKg = stableDelta,
            FuelState = verified
        };
    }

    private async Task<AarBridgeResponse> SendAsync(string action, double? deltaKg = null,
        CancellationToken cancellationToken = default, bool allowFailedResponse = false, string? writeMethod = null)
    {
        if (_client is null) throw new InvalidOperationException("Connect to MSFS first.");
        var request = new AarBridgeRequest(Guid.NewGuid().ToString("N"), action, 1, "local-aar-fuel-tester", deltaKg, writeMethod);
        var response = await _client.SendAsync(request, cancellationToken);
        if (!string.Equals(response.RequestId, request.RequestId, StringComparison.Ordinal) ||
            !string.Equals(response.Action, action, StringComparison.Ordinal))
            throw new InvalidDataException("The AAR bridge response did not match the request.");
        if (!allowFailedResponse && string.Equals(response.Status, "Failed", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(response.Error ?? $"Bridge request {action} failed.");
        return response;
    }

    private void ShowFuelState(AarBridgeResponse response, bool logTanks = true)
    {
        if (response.FuelState is not { } fuel) throw new InvalidDataException("The bridge returned no fuel state.");
        _lastFuelState = fuel;
        _hasFuelState = fuel.Tanks.Length > 0;
        StatusText.Text = $"Bridge {response.BridgeVersion ?? "connected"} · Current {fuel.CurrentFuelKg:0.00} kg / {fuel.CapacityKg:0.00} kg\n" +
            $"Tanks: {fuel.Tanks.Length} · all tank quantities are returned as verified simulator read-back.";
        if (logTanks)
            foreach (var tank in fuel.Tanks)
                Append($"{tank.TankId}: {tank.CurrentKg:0.00} / {tank.CapacityKg:0.00} kg · writable={tank.Writable}");
        UpdateButtons();
    }

    private void UpdateButtons()
    {
        var enabled = !TransferProgress.IsVisible && _client?.IsConnected == true && _fuelRead && _fuelWrite && _hasFuelState;
        AddButton.IsEnabled = enabled;
        RemoveButton.IsEnabled = enabled;
        StopButton.IsEnabled = _transferCts is not null;
    }

    private void SetBusy(bool busy, string status)
    {
        ConnectButton.IsEnabled = !busy;
        SimConnectPathBox.IsEnabled = !busy;
        AddButton.IsEnabled = !busy && _client?.IsConnected == true && _fuelRead && _fuelWrite && _hasFuelState;
        RemoveButton.IsEnabled = AddButton.IsEnabled;
        StopButton.IsEnabled = busy && _transferCts is not null;
        StatusText.Text = status;
    }

    private void Append(string message)
    {
        LogText.AppendText($"{DateTime.Now:HH:mm:ss}  {message}{Environment.NewLine}");
        LogText.ScrollToEnd();
    }

    protected override void OnClosed(EventArgs e)
    {
        _transferCts?.Cancel();
        _dispatchTimer.Stop();
        _client?.Dispose();
        base.OnClosed(e);
    }
}

internal sealed class SimConnectCommBusClient : IDisposable
{
    private const string RequestEvent = "VTSD_AAR_REQUEST";
    private const string ResponseEvent = "VTSD_AAR_RESPONSE";
    private const uint ResponseEventId = 1500;
    private const uint CommBusBroadcastToWasm = 1 << 1;
    private const uint CommBusReceiveId = 43;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly Dictionary<string, TaskCompletionSource<AarBridgeResponse>> _pending = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    private readonly StringBuilder _responseBuffer = new();
    private uint _responseParts;
    private IntPtr _handle;
    private Api? _api;
    public bool IsConnected => _handle != IntPtr.Zero && _api is not null;

    public SimConnectCommBusClient(string dllPath) => DllPath = dllPath;
    private string DllPath { get; }

    public void Connect()
    {
        if (string.IsNullOrWhiteSpace(DllPath) || !File.Exists(DllPath))
            throw new FileNotFoundException("SimConnect.dll was not found. Select the MSFS 2024 SDK SimConnect DLL.", DllPath);
        _api = Api.Load(DllPath);
        if (_api.CallCommBusEvent is null || _api.SubscribeToCommBusEvent is null)
            throw new NotSupportedException("This SimConnect.dll does not export the MSFS 2024 CommBus functions.");
        var result = _api.Open(out _handle, "VTSD Local AAR Fuel Tester", IntPtr.Zero, 0, IntPtr.Zero, 0);
        if (result != 0) throw new IOException($"SimConnect_Open failed: 0x{result:X8}.");
        result = _api.SubscribeToCommBusEvent(_handle, ResponseEventId, ResponseEvent);
        if (result != 0)
        {
            _api.Close(_handle);
            _handle = IntPtr.Zero;
            throw new IOException($"SimConnect_CommBus subscription failed: 0x{result:X8}.");
        }
    }

    public async Task<AarBridgeResponse> SendAsync(AarBridgeRequest request, CancellationToken token)
    {
        if (!IsConnected || _api?.CallCommBusEvent is null) throw new InvalidOperationException("SimConnect is not connected.");
        var completion = new TaskCompletionSource<AarBridgeResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate) _pending.Add(request.RequestId, completion);
        try
        {
            var payload = JsonSerializer.SerializeToUtf8Bytes(request, Json);
            var terminated = new byte[payload.Length + 1];
            Buffer.BlockCopy(payload, 0, terminated, 0, payload.Length);
            var data = Marshal.AllocHGlobal(terminated.Length);
            try
            {
                Marshal.Copy(terminated, 0, data, terminated.Length);
                var result = _api.CallCommBusEvent(_handle, RequestEvent, CommBusBroadcastToWasm, (uint)terminated.Length, data);
                if (result != 0) throw new IOException($"SimConnect_CallCommBusEvent failed: 0x{result:X8}.");
            }
            finally { Marshal.FreeHGlobal(data); }
            return await completion.Task.WaitAsync(TimeSpan.FromSeconds(5), token);
        }
        finally { lock (_gate) _pending.Remove(request.RequestId); }
    }

    public void Dispatch()
    {
        if (_api is null || _handle == IntPtr.Zero) return;
        while (_api.GetNextDispatch(_handle, out var data, out var size) == 0)
        {
            if (data == IntPtr.Zero || size < 32) continue;
            var header = Marshal.PtrToStructure<CommBusHeader>(data);
            if (header.Id != CommBusReceiveId || header.EventId != ResponseEventId || header.Parts == 0 || header.PartIndex >= header.Parts) continue;
            var bytes = new byte[checked((int)size - 32)];
            Marshal.Copy(IntPtr.Add(data, 32), bytes, 0, bytes.Length);
            var chunk = Encoding.UTF8.GetString(bytes).TrimEnd('\0');
            string? complete = null;
            lock (_gate)
            {
                if (header.PartIndex == 0)
                {
                    _responseBuffer.Clear();
                    _responseParts = header.Parts;
                }
                if (_responseParts != header.Parts) continue;
                _responseBuffer.Append(chunk);
                if (header.PartIndex + 1 == header.Parts)
                {
                    complete = _responseBuffer.ToString();
                    _responseBuffer.Clear();
                    _responseParts = 0;
                }
            }
            if (complete is null) continue;
            try
            {
                var response = JsonSerializer.Deserialize<AarBridgeResponse>(complete, Json);
                if (response?.RequestId is { Length: > 0 } id)
                {
                    lock (_gate)
                        if (_pending.TryGetValue(id, out var completion)) completion.TrySetResult(response);
                }
            }
            catch (JsonException) { }
        }
    }

    public void Dispose()
    {
        if (_handle != IntPtr.Zero) _api?.Close(_handle);
        _handle = IntPtr.Zero;
        _api?.Dispose();
        _api = null;
        lock (_gate)
        {
            foreach (var completion in _pending.Values) completion.TrySetException(new IOException("SimConnect closed."));
            _pending.Clear();
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CommBusHeader
    {
        public uint Size;
        public uint Version;
        public uint Id;
        public uint RequestId;
        public uint ArraySize;
        public uint PartIndex;
        public uint Parts;
        public uint EventId;
    }

    private sealed class Api : IDisposable
    {
        private readonly IntPtr _library;
        private Api(IntPtr library, OpenDelegate open, CloseDelegate close, GetNextDispatchDelegate getNextDispatch,
            CallCommBusEventDelegate? callCommBusEvent, SubscribeToCommBusEventDelegate? subscribeToCommBusEvent)
        {
            _library = library;
            Open = open;
            Close = close;
            GetNextDispatch = getNextDispatch;
            CallCommBusEvent = callCommBusEvent;
            SubscribeToCommBusEvent = subscribeToCommBusEvent;
        }
        public OpenDelegate Open { get; }
        public CloseDelegate Close { get; }
        public GetNextDispatchDelegate GetNextDispatch { get; }
        public CallCommBusEventDelegate? CallCommBusEvent { get; }
        public SubscribeToCommBusEventDelegate? SubscribeToCommBusEvent { get; }

        public static Api Load(string path)
        {
            var library = NativeLibrary.Load(path);
            try
            {
                T Get<T>(string name) where T : Delegate => Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(library, name));
                T? TryGet<T>(string name) where T : Delegate => NativeLibrary.TryGetExport(library, name, out var fn) ? Marshal.GetDelegateForFunctionPointer<T>(fn) : null;
                return new Api(library, Get<OpenDelegate>("SimConnect_Open"), Get<CloseDelegate>("SimConnect_Close"),
                    Get<GetNextDispatchDelegate>("SimConnect_GetNextDispatch"), TryGet<CallCommBusEventDelegate>("SimConnect_CallCommBusEvent"),
                    TryGet<SubscribeToCommBusEventDelegate>("SimConnect_SubscribeToCommBusEvent"));
            }
            catch { NativeLibrary.Free(library); throw; }
        }

        public void Dispose() => NativeLibrary.Free(_library);
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Ansi)]
    public delegate int OpenDelegate(out IntPtr handle, string name, IntPtr window, uint userEvent, IntPtr eventHandle, uint configIndex);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    public delegate int CloseDelegate(IntPtr handle);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    public delegate int GetNextDispatchDelegate(IntPtr handle, out IntPtr data, out uint size);
    [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Ansi)]
    public delegate int CallCommBusEventDelegate(IntPtr handle, string eventName, uint broadcastTo, uint bufferSize, IntPtr data);
    [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Ansi)]
    public delegate int SubscribeToCommBusEventDelegate(IntPtr handle, uint eventId, string eventName);
}

using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json;
using System.Windows;
using TacticalDisplay.App.Commands;
using TacticalDisplay.App.Data;
using TacticalDisplay.App.Services;
using TacticalDisplay.App.TacticalLink;
using TacticalDisplay.Core.Config;
using TacticalDisplay.Core.Models;

namespace TacticalDisplay.App.ViewModels;

/// <summary>Owns AAR module presentation state and its module commands.</summary>
public sealed class AarViewModel : ViewModelBase
{
    private readonly AarClient _client;
    private readonly AarFuelTransferCoordinator _fuelCoordinator;
    private readonly TacticalLinkClient _link;
    private readonly TacticalDisplaySettings _settings;
    private readonly Func<IAarFuelAdapter?> _fuelAdapter;
    private readonly Func<IReadOnlyList<TacticalLinkPeerDisplay>> _peers;
    private readonly Func<bool> _isTankerCapable;
    private readonly Func<bool> _isReceiverCapable;
    private readonly Func<bool> _isConnected;
    private readonly Action _stateChanged;
    private readonly CancellationToken _token;
    private readonly HashSet<string> _activeOperations = new(StringComparer.Ordinal);
    private readonly ObservableCollection<AarPendingRequestDisplay> _pendingRequests = [];
    private string _reserveText = "0";
    private string _requestedText = "1000";
    private string _plannedText = string.Empty;
    private string _statusText = "AAR unavailable";
    private string _operationStateText = "No active operation";
    private string _transferMode = "Fuel";
    private string _tankerMetricsText = "Fuel adapter unavailable";
    private string _unit;
    private TacticalLinkPeerDisplay? _selectedTanker;
    private TacticalLinkPeerDisplay? _selectedReceiver;
    private AarPendingRequestDisplay? _selectedRequest;
    private string? _activeOperationId;
    private string? _ownPendingRequestId;
    private string? _committedNextOperationId;
    private bool _activeIsLocalTanker;
    private double _protectedReserveKg;
    private JsonElement? _fuelSummary;
    private (double TransferredKg, double RemainingKg, double FlowKgPerSecond, bool IsRefueling)? _transferMetrics;

    internal AarViewModel(AarClient client, AarFuelTransferCoordinator fuelCoordinator, TacticalLinkClient link,
        TacticalDisplaySettings settings, Func<IAarFuelAdapter?> fuelAdapter,
        Func<IReadOnlyList<TacticalLinkPeerDisplay>> peers, Func<bool> isTankerCapable,
        Func<bool> isReceiverCapable, Func<bool> isConnected,
        Action stateChanged, CancellationToken token)
    {
        _client = client;
        _fuelCoordinator = fuelCoordinator;
        _link = link;
        _settings = settings;
        _fuelAdapter = fuelAdapter;
        _peers = peers;
        _isTankerCapable = isTankerCapable;
        _isReceiverCapable = isReceiverCapable;
        _isConnected = isConnected;
        _stateChanged = stateChanged;
        _token = token;
        _unit = string.Equals(settings.AarFuelUnit, "LB", StringComparison.OrdinalIgnoreCase) ? "LB" : "KG";
        _client.EventReceived += OnModuleEventReceived;

        SetAarProtectedReserveCommand = Command(() => _ = SetReserveAsync());
        RequestFullAarCommand = Command(() => _ = RequestFullAsync());
        AcceptAarRequestCommand = Command(() => _ = RespondAsync(true));
        RejectAarRequestCommand = Command(() => _ = RespondAsync(false));
        ReturnAarCommittedToPendingCommand = Command(() => _ = ReturnCommittedAsync());
        CancelOwnAarRequestCommand = Command(() => _ = CancelRequestAsync());
        RequestAarCommand = Command(() => _ = RequestAsync());
        AddAarReceiverCommand = Command(() => _ = AddReceiverAsync());
        RemoveAarQueueEntryCommand = Command(() => _ = RemoveQueueEntryAsync());
        MoveAarQueueEntryUpCommand = Command(() => _ = MoveQueueEntryAsync(-1));
        MoveAarQueueEntryDownCommand = Command(() => _ = MoveQueueEntryAsync(1));
        SetAarPlannedOnloadCommand = Command(() => _ = SetPlanAsync());
        ToggleAarTransferModeCommand = Command(() => _ = ToggleModeAsync());
        ClearAarAsternCommand = Command(() => _ = SendOperationCommandAsync("CLEAR_ASTERN"));
        HoldAarOperationCommand = Command(() => _ = SendOperationCommandAsync("HOLD"));
        StartAarTransferCommand = Command(() => _ = SendOperationCommandAsync("START_TRANSFER"));
        StopAarTransferCommand = Command(() => _ = SendOperationCommandAsync("STOP_TRANSFER"));
        StartAarPrecontactCommand = Command(() => _ = SendOperationCommandAsync("PROCEED_TO_PRECONTACT"));
        ClearAarContactCommand = Command(() => _ = SendOperationCommandAsync("CLEAR_CONTACT"));
        DisconnectAarOperationCommand = Command(() => _ = SendOperationCommandAsync("DISCONNECT"));
        BreakawayAarOperationCommand = Command(() => _ = SendOperationCommandAsync("BREAKAWAY"));
        ReconcileAarOperationCommand = Command(() => _ = SendOperationCommandAsync("RECONCILE"));
        ToggleAarAvailabilityCommand = Command(() => _ = ToggleAvailabilityAsync());
        ToggleTankerAvailabilityCommand = Command(() => _ = ToggleTankerModeAsync());
    }

    private RelayCommand Command(Action action) => new(action);
    public RelayCommand SetAarProtectedReserveCommand { get; }
    public RelayCommand RequestFullAarCommand { get; }
    public RelayCommand AcceptAarRequestCommand { get; }
    public RelayCommand RejectAarRequestCommand { get; }
    public RelayCommand ReturnAarCommittedToPendingCommand { get; }
    public RelayCommand CancelOwnAarRequestCommand { get; }
    public RelayCommand RequestAarCommand { get; }
    public RelayCommand AddAarReceiverCommand { get; }
    public RelayCommand RemoveAarQueueEntryCommand { get; }
    public RelayCommand MoveAarQueueEntryUpCommand { get; }
    public RelayCommand MoveAarQueueEntryDownCommand { get; }
    public RelayCommand SetAarPlannedOnloadCommand { get; }
    public RelayCommand ToggleAarTransferModeCommand { get; }
    public RelayCommand ClearAarAsternCommand { get; }
    public RelayCommand HoldAarOperationCommand { get; }
    public RelayCommand StartAarTransferCommand { get; }
    public RelayCommand StopAarTransferCommand { get; }
    public RelayCommand StartAarPrecontactCommand { get; }
    public RelayCommand ClearAarContactCommand { get; }
    public RelayCommand DisconnectAarOperationCommand { get; }
    public RelayCommand BreakawayAarOperationCommand { get; }
    public RelayCommand ReconcileAarOperationCommand { get; }
    public RelayCommand ToggleAarAvailabilityCommand { get; }
    public RelayCommand ToggleTankerAvailabilityCommand { get; }

    public IReadOnlyList<TacticalLinkPeerDisplay> AvailableAarTankers => _peers().Where(peer =>
        _link.NearbyPeers.FirstOrDefault(candidate => candidate.ParticipantId == peer.ParticipantId) is { } actual &&
        actual.Capabilities.Contains("aar.tanker") && actual.OperationalStates.TryGetValue("tankerAvailability", out var state) && state == "Available").ToArray();
    public IReadOnlyList<TacticalLinkPeerDisplay> AvailableAarReceivers => _peers().Where(peer =>
        _link.NearbyPeers.FirstOrDefault(candidate => candidate.ParticipantId == peer.ParticipantId)?.Capabilities.Contains("aar.receiver") == true).ToArray();
    public ObservableCollection<AarPendingRequestDisplay> AarPendingRequests => _pendingRequests;
    public TacticalLinkPeerDisplay? SelectedAarTanker { get => _selectedTanker; set => SetField(ref _selectedTanker, value); }
    public TacticalLinkPeerDisplay? SelectedAarReceiver { get => _selectedReceiver; set => SetField(ref _selectedReceiver, value); }
    public AarPendingRequestDisplay? SelectedAarRequest { get => _selectedRequest; set => SetField(ref _selectedRequest, value); }
    public string AarProtectedReserveKgText { get => _reserveText; set => SetField(ref _reserveText, value); }
    public string AarRequestedAmountText { get => _requestedText; set => SetField(ref _requestedText, value); }
    public string AarPlannedAmountText { get => _plannedText; set => SetField(ref _plannedText, value); }
    public string AarFuelUnitLabel => _unit;
    public double ProtectedReserveKg => _protectedReserveKg;
    public string AarStatusText { get => _statusText; private set => SetField(ref _statusText, value); }
    public string AarOperationStateText { get => _operationStateText; private set => SetField(ref _operationStateText, value); }
    public string AarTransferMode => _transferMode;
    public string AarTankerMetricsText { get => _tankerMetricsText; private set => SetField(ref _tankerMetricsText, value); }
    public string TankerButtonText => _client.IsTankerJoined ? "LEAVE TANKER MODE" : "JOIN AS TANKER";
    public string AarAvailabilityButtonText => _client.IsTankerAvailable ? "SET UNAVAILABLE" : "SET AVAILABLE";
    public bool HasAarOperation => _activeOperationId is not null;
    public bool HasActiveOperations => _activeOperations.Count > 0;
    public bool IsLocalTankerForActiveOperation => _activeIsLocalTanker;
    public bool HasAarCommittedNext => _committedNextOperationId is not null;
    public bool HasOwnAarPendingRequest => _ownPendingRequestId is not null;
    public bool IsAarReceiverCapable => _isReceiverCapable() && _isConnected();
    public bool IsAarTankerModeJoined => _client.IsTankerJoined;
    public bool IsLocalTankerJoined => _client.IsTankerJoined;
    public bool IsTankerCapable => _isTankerCapable();
    public bool CanUseAar => _isConnected() && (_isTankerCapable() || _isReceiverCapable());
    public bool CanOfferTanker => _isConnected() && _isTankerCapable();
    public string AarFuelUnit
    {
        get => _unit;
        set
        {
            var normalized = string.Equals(value, "LB", StringComparison.OrdinalIgnoreCase) ? "LB" : "KG";
            if (_unit == normalized) return;
            var previous = _unit;
            AarProtectedReserveKgText = ConvertUnit(AarProtectedReserveKgText, previous, normalized);
            AarRequestedAmountText = ConvertUnit(AarRequestedAmountText, previous, normalized);
            AarPlannedAmountText = ConvertUnit(AarPlannedAmountText, previous, normalized);
            _unit = normalized;
            _settings.AarFuelUnit = normalized;
            Raise(); Raise(nameof(AarFuelUnitLabel));
            if (_transferMetrics is { } metrics) AarTankerMetricsText = FormatMetrics(metrics.TransferredKg, metrics.RemainingKg, metrics.FlowKgPerSecond, metrics.IsRefueling);
            else if (_fuelSummary is { } summary) AarTankerMetricsText = FormatFuelSummary(summary);
        }
    }

    public void OnPose(OwnshipState sample) => ObservePublish(_client.PublishPoseAsync(sample, _token), "pose");

    public void OnFuel(AarFuelReading reading)
    {
        if (!_isConnected() || (!_isTankerCapable() && !_isReceiverCapable())) return;
        var adapter = _fuelAdapter();
        var ready = adapter is { IsAvailable: true, CanReadFuel: true, CanWriteFuel: true };
        var watermark = _activeOperationId is { } id ? _fuelCoordinator.GetWatermark(id) : 0;
        ObservePublish(_client.PublishFuelStatusAsync(reading.CurrentFuelKg, reading.CapacityKg, ready, watermark, _token), "fuel-status");
    }

    private static void ObservePublish(Task task, string kind) => _ = task.ContinueWith(completed =>
    {
        if (completed.Exception is { } error)
            DataSourceDebugLog.ThrottledDebug("AAR", $"{kind}-send", TimeSpan.FromSeconds(10), () => $"AAR {kind} send failed: {error.GetBaseException().Message}");
    }, TaskContinuationOptions.OnlyOnFaulted);

    public void SetConnectedOperationSampling(bool connected, IAarPoseSource? poseSource)
    {
        if (poseSource is not null) poseSource.AarSamplingEnabled = connected && _activeOperations.Count > 0;
    }

    public void NotifyContextChanged()
    {
        Raise(nameof(AvailableAarTankers)); Raise(nameof(AvailableAarReceivers)); Raise(nameof(CanUseAar));
        Raise(nameof(CanOfferTanker)); Raise(nameof(IsAarReceiverCapable)); Raise(nameof(IsTankerCapable));
        Raise(nameof(IsLocalTankerJoined)); Raise(nameof(TankerButtonText)); Raise(nameof(AarAvailabilityButtonText));
    }

    public void SetFuelAdapterUnavailable() => AarTankerMetricsText = "Fuel adapter unavailable";

    public void HandleEvent(TacticalLinkModuleEvent message) => ApplyModuleEvent(message);

    private void OnModuleEventReceived(object? sender, TacticalLinkModuleEvent message) => HandleEvent(message);

    public void Dispose() => _client.EventReceived -= OnModuleEventReceived;

    private string FormatFuelSummary(JsonElement fuel)
    {
        if (!fuel.TryGetProperty("currentFuelKg", out var current) || current.ValueKind != JsonValueKind.Number ||
            !fuel.TryGetProperty("protectedReserveKg", out var reserve) || reserve.ValueKind != JsonValueKind.Number ||
            !fuel.TryGetProperty("availableToPromiseKg", out var available) || available.ValueKind != JsonValueKind.Number)
            return "Fuel adapter unavailable";
        return $"Fuel {FormatAmount(current.GetDouble())} {_unit} · reserve {FormatAmount(reserve.GetDouble())} {_unit} · available {FormatAmount(available.GetDouble())} {_unit}";
    }

    private string FormatMetrics(double transferred, double remaining, double flow, bool isRefueling) =>
        isRefueling && flow > 0
            ? $"Transferred {FormatAmount(transferred)} {_unit} · remaining {FormatAmount(remaining)} {_unit} · {FormatFlow(flow)} {_unit}/s · ETA {TimeSpan.FromSeconds(remaining / flow):mm\\:ss}"
            : $"Transferred {FormatAmount(transferred)} {_unit} · remaining {FormatAmount(remaining)} {_unit} · transfer paused";
    private double FormatAmount(double kg) => _unit == "LB" ? kg * 2.2046226218 : kg;
    private double FormatFlow(double kgps) => _unit == "LB" ? kgps * 2.2046226218 : kgps;
    private static string ConvertUnit(string text, string from, string to)
    {
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) || !double.IsFinite(value)) return text;
        var kg = from == "LB" ? value / 2.2046226218 : value;
        return (to == "LB" ? kg * 2.2046226218 : kg).ToString("0.##", CultureInfo.InvariantCulture);
    }
    private bool TryParseAmount(string text, out double kg)
    {
        kg = 0;
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) || !double.IsFinite(value)) return false;
        kg = _unit == "LB" ? value / 2.2046226218 : value;
        return double.IsFinite(kg);
    }

    private async Task SetReserveAsync()
    {
        if (!_isTankerCapable() || !_isConnected() || !_client.IsTankerJoined) return;
        if (!TryParseAmount(AarProtectedReserveKgText, out var kg) || kg < 0) { AarStatusText = $"Enter reserve fuel in {_unit}"; return; }
        await _client.SetProtectedReserveAsync(kg, _token);
        _protectedReserveKg = kg;
        AarStatusText = $"Protected reserve set: {FormatAmount(kg)} {_unit}";
    }
    private async Task ToggleTankerModeAsync()
    {
        if (!_isTankerCapable() || !_isConnected()) return;
        if (_client.IsTankerJoined) { await _client.LeaveTankerAsync(_token); AarStatusText = "Tanker mode off"; }
        else { await _client.JoinTankerAsync(_token); AarStatusText = "Tanker joined; set reserve and check adapter readiness"; }
        Raise(nameof(TankerButtonText)); Raise(nameof(IsLocalTankerJoined));
    }
    private async Task ToggleAvailabilityAsync()
    {
        if (!_isTankerCapable() || !_isConnected() || !_client.IsTankerJoined) return;
        await _client.SetTankerAvailabilityAsync(!_client.IsTankerAvailable, _token);
        Raise(nameof(AarAvailabilityButtonText));
    }
    private async Task RequestFullAsync()
    {
        if (!IsAarReceiverCapable || SelectedAarTanker is null) return;
        await _client.RequestRefuelAsync(SelectedAarTanker.ParticipantId, null, true, AarTransferMode, _token);
        AarStatusText = "FULL request sent";
    }
    private async Task RequestAsync()
    {
        if (!IsAarReceiverCapable || SelectedAarTanker is null) return;
        if (AarTransferMode == "DryHookup")
        {
            await _client.RequestRefuelAsync(SelectedAarTanker.ParticipantId, null, true, AarTransferMode, _token);
            AarStatusText = "Dry Hookup request sent";
            return;
        }
        if (!TryParseAmount(AarRequestedAmountText, out var kg) || kg <= 0) { AarStatusText = $"Enter a positive request amount in {_unit}"; return; }
        await _client.RequestRefuelAsync(SelectedAarTanker.ParticipantId, kg, false, AarTransferMode, _token);
        AarStatusText = "Refuel request sent";
    }
    private async Task AddReceiverAsync()
    {
        if (!_client.IsTankerJoined || SelectedAarReceiver is null) return;
        await _client.AddReceiverAsync(SelectedAarReceiver.ParticipantId, AarTransferMode, _token);
    }
    private async Task RemoveQueueEntryAsync()
    {
        if (SelectedAarRequest is null) return;
        await _client.RemoveQueueEntryAsync(SelectedAarRequest.RequestId, _token);
        _pendingRequests.Remove(SelectedAarRequest); SelectedAarRequest = null;
    }
    private async Task MoveQueueEntryAsync(int offset)
    {
        if (SelectedAarRequest is null) return;
        var current = _pendingRequests.IndexOf(SelectedAarRequest);
        var position = Math.Clamp(current + offset, 0, Math.Max(0, _pendingRequests.Count - 1));
        await _client.MoveQueueEntryAsync(SelectedAarRequest.RequestId, position, _token);
    }
    private async Task ReturnCommittedAsync()
    {
        if (!_client.IsTankerJoined || _committedNextOperationId is not { } operationId) return;
        await _client.ReturnCommittedToPendingAsync(operationId, _token);
    }
    private async Task CancelRequestAsync()
    {
        if (_ownPendingRequestId is not { } requestId) return;
        await _client.CancelRequestAsync(requestId, _token);
        _ownPendingRequestId = null; Raise(nameof(HasOwnAarPendingRequest));
    }
    private async Task SetPlanAsync()
    {
        if (!_isConnected() || _activeOperationId is not { } id) return;
        if (!TryParseAmount(AarPlannedAmountText, out var kg) || kg <= 0) { AarStatusText = $"Enter a positive planned amount in {_unit}"; return; }
        await _client.SetPlannedOnloadAsync(id, kg, _token);
    }
    private async Task ToggleModeAsync()
    {
        if (!_isConnected() || _activeOperationId is not { } id) return;
        _transferMode = _transferMode == "Fuel" ? "DryHookup" : "Fuel";
        await _client.SetTransferModeAsync(id, _transferMode, _token);
        Raise(nameof(AarTransferMode));
    }
    private async Task SendOperationCommandAsync(string kind)
    {
        if (!_isConnected() || _activeOperationId is not { } id) return;
        var task = kind switch
        {
            "CLEAR_ASTERN" => _client.ClearAsternAsync(id, _token),
            "HOLD" => _client.HoldAsync(id, _token),
            "CLEAR_CONTACT" => _client.ClearContactAsync(id, _token),
            "START_TRANSFER" => _client.StartTransferAsync(id, _token),
            "STOP_TRANSFER" => _client.StopTransferAsync(id, _token),
            "DISCONNECT" => _client.DisconnectAsync(id, _token),
            "BREAKAWAY" => _client.BreakawayAsync(id, _token),
            "RECONCILE" => _client.ReconcileAsync(id, _token),
            _ => Task.FromException<string>(new ArgumentOutOfRangeException(nameof(kind)))
        };
        await task;
    }
    private async Task RespondAsync(bool accept)
    {
        if (!_isTankerCapable() || !_isConnected() || !_client.IsTankerJoined || SelectedAarRequest is null) return;
        double? plan = null;
        if (accept && !string.IsNullOrWhiteSpace(AarPlannedAmountText))
        {
            if (!TryParseAmount(AarPlannedAmountText, out var kg) || kg <= 0) { AarStatusText = $"Enter a positive planned amount in {_unit}, or leave it blank to use the safe maximum"; return; }
            plan = kg;
        }
        await _client.RespondToRequestAsync(SelectedAarRequest.RequestId, accept, AarTransferMode, plan, _token);
        AarStatusText = accept ? "Request accepted" : "Request rejected";
        if (!accept) _pendingRequests.Remove(SelectedAarRequest);
        SelectedAarRequest = null;
    }

    public void ApplyModuleEvent(TacticalLinkModuleEvent e)
    {
        if (e.Module != "aar" || e.Kind.StartsWith("POSE_", StringComparison.Ordinal)) return;
        Application.Current.Dispatcher.Invoke(() =>
        {
            ApplyModuleEventCore(e);
            _stateChanged();
        });
    }

    private void ApplyModuleEventCore(TacticalLinkModuleEvent e)
    {
        var fuel = e.Payload.TryGetProperty("fuel", out var nested) ? nested : e.Payload;
        if (fuel.TryGetProperty("protectedReserveKg", out var reserve) && reserve.TryGetDouble(out var reserveKg) && reserveKg >= 0) _protectedReserveKg = reserveKg;
        if (e.OperationId is { } opId && e.Payload.TryGetProperty("transferredKg", out var watermark) && watermark.TryGetDouble(out var cumulative)) _fuelCoordinator.RecordWatermark(opId, cumulative);
        if (e.OperationId is { } id)
        {
            if (e.Payload.TryGetProperty("transferMode", out var mode) && mode.GetString() is { } value) { _transferMode = value; Raise(nameof(AarTransferMode)); }
            var state = e.Payload.TryGetProperty("state", out var stateNode) ? stateNode.GetString() :
                e.Payload.TryGetProperty("operation", out var operation) && operation.TryGetProperty("state", out var nestedState) ? nestedState.GetString() : null;
            var slot = e.Payload.TryGetProperty("slot", out var slotNode) ? slotNode.GetString() :
                e.Payload.TryGetProperty("operation", out var operation2) && operation2.TryGetProperty("slot", out var nestedSlot) ? nestedSlot.GetString() : null;
            var live = state is "Accepted" or "PreContact" or "Astern" or "ClearedContact" or "Contact" or "Refueling" or "Suspended";
            if (live && (slot == "Active" || _activeOperationId is null))
            {
                _activeOperationId = id;
                _activeIsLocalTanker = e.Payload.TryGetProperty("tankerParticipantId", out var tankerId) && tankerId.GetString() == _link.LocalParticipantId;
            }
            if (live) _activeOperations.Add(id);
            if (state is not null)
            {
                AarOperationStateText = state.Replace('_', ' ').ToUpperInvariant();
                if (state is "Complete" or "Failed" or "Breakaway" or "Cancelled")
                {
                    _activeOperations.Remove(id);
                    if (_activeOperationId == id) { _activeOperationId = null; _activeIsLocalTanker = false; }
                }
            }
        }
        if (e.Kind == "AAR_STATE" && e.Payload.TryGetProperty("operations", out var operations) && operations.ValueKind == JsonValueKind.Array)
        {
            _activeOperations.Clear(); _activeOperationId = null;
            foreach (var operation in operations.EnumerateArray())
            {
                if (!operation.TryGetProperty("operationId", out var idNode) || idNode.GetString() is not { } currentId || !operation.TryGetProperty("state", out var stateNode) || stateNode.GetString() is not { } currentState) continue;
                if (currentState is "Accepted" or "PreContact" or "Astern" or "ClearedContact" or "Contact" or "Refueling" or "Suspended") _activeOperations.Add(currentId);
                if (operation.TryGetProperty("slot", out var slot) && slot.GetString() == "Active")
                {
                    _activeOperationId = currentId; _activeIsLocalTanker = operation.TryGetProperty("tankerParticipantId", out var tanker) && tanker.GetString() == _link.LocalParticipantId;
                    AarOperationStateText = currentState.Replace('_', ' ').ToUpperInvariant();
                }
            }
        }
        if (e.Kind == "REQUEST_QUEUED" && e.Payload.TryGetProperty("requestId", out var requestId) && requestId.GetString() is { } queuedId)
        {
            var receiverId = e.Payload.TryGetProperty("receiverParticipantId", out var receiver) ? receiver.GetString() : null;
            var callsign = _link.NearbyPeers.FirstOrDefault(peer => peer.ParticipantId == receiverId)?.Callsign ?? "Receiver";
            _pendingRequests.Add(new AarPendingRequestDisplay(queuedId, callsign, "Pending"));
            if (receiverId == _link.LocalParticipantId) _ownPendingRequestId = queuedId;
        }
        else if ((e.Kind is "TANKER_ADDED_RECEIVER" or "REQUEST_PENDING") && e.Payload.TryGetProperty("requestId", out var ownRequest) && ownRequest.GetString() is { } ownId) _ownPendingRequestId = ownId;
        else if ((e.Kind is "AAR_STATE" or "QUEUE_UPDATED") && e.Payload.TryGetProperty("queue", out var queue) && queue.ValueKind == JsonValueKind.Object && queue.TryGetProperty("pending", out var pending) && pending.ValueKind == JsonValueKind.Array)
        {
            _committedNextOperationId = queue.TryGetProperty("committedNext", out var committed) && committed.ValueKind == JsonValueKind.Object && committed.TryGetProperty("operationId", out var committedId) ? committedId.GetString() : null;
            _pendingRequests.Clear();
            foreach (var item in pending.EnumerateArray())
            {
                if (!item.TryGetProperty("requestId", out var idNode) || idNode.GetString() is not { } pendingId) continue;
                var participant = item.TryGetProperty("receiverParticipantId", out var receiverId) ? receiverId.GetString() : null;
                _pendingRequests.Add(new AarPendingRequestDisplay(pendingId, _link.NearbyPeers.FirstOrDefault(peer => peer.ParticipantId == participant)?.Callsign ?? "Receiver", "Pending"));
            }
        }
        else if ((e.Kind is "REQUEST_CANCELLED" or "REQUEST_REJECTED" or "REQUEST_ACCEPTED") && e.Payload.TryGetProperty("requestId", out var ended) && ended.GetString() is { } endedId)
        {
            var item = _pendingRequests.FirstOrDefault(request => request.RequestId == endedId);
            if (item is not null) _pendingRequests.Remove(item);
            if (_ownPendingRequestId == endedId) _ownPendingRequestId = null;
        }
        AarStatusText = e.Kind switch
        {
            "TANKER_MODE_JOINED" => "Tanker joined; set reserve and check adapter readiness",
            "TANKER_MODE_LEFT" => "Tanker mode off",
            "RESERVE_UPDATED" => "Protected reserve updated",
            "TANKER_AVAILABILITY_UPDATED" => _client.IsTankerAvailable ? "Tanker available for requests" : "Tanker unavailable",
            "MODULE_ERROR" => e.Payload.TryGetProperty("message", out var message) ? message.GetString() ?? "AAR command failed" : "AAR command failed",
            "OPERATION_SUSPENDED" => "AAR stopped; reconnect and reconcile before continuing",
            "OPERATION_FAILED" => "AAR operation failed",
            _ => e.Kind.Replace('_', ' ')
        };
        if (_isTankerCapable() && e.Payload.TryGetProperty("fuel", out var summary)) { _fuelSummary = summary.Clone(); AarTankerMetricsText = FormatFuelSummary(summary); }
        else if (_isTankerCapable() && e.Kind == "RESERVE_UPDATED") { _fuelSummary = e.Payload.Clone(); AarTankerMetricsText = FormatFuelSummary(e.Payload); }
        if (_isTankerCapable() && e.Payload.TryGetProperty("transferredKg", out var transferredNode) && transferredNode.TryGetDouble(out var transferred) &&
            e.Payload.TryGetProperty("remainingKg", out var remainingNode) && remainingNode.TryGetDouble(out var remaining) &&
            e.Payload.TryGetProperty("effectiveFlowKgPerSecond", out var flowNode) && flowNode.TryGetDouble(out var flow))
        {
            var refueling = e.Payload.TryGetProperty("state", out var state) && state.GetString() == "Refueling" && flow > 0;
            _transferMetrics = (transferred, remaining, flow, refueling);
            AarTankerMetricsText = FormatMetrics(transferred, remaining, flow, refueling);
        }
        Raise(nameof(AvailableAarTankers)); Raise(nameof(AvailableAarReceivers)); Raise(nameof(HasOwnAarPendingRequest));
        Raise(nameof(HasAarCommittedNext)); Raise(nameof(HasAarOperation)); Raise(nameof(IsLocalTankerForActiveOperation));
        Raise(nameof(IsLocalTankerJoined)); Raise(nameof(AarAvailabilityButtonText)); Raise(nameof(TankerButtonText));
    }
}

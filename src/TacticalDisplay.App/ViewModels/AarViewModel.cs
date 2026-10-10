using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using TacticalDisplay.App.Commands;
using TacticalDisplay.App.Data;
using TacticalDisplay.App.Services;
using TacticalDisplay.App.TacticalLink;
using TacticalDisplay.Core.Config;
using TacticalDisplay.Core.Models;

namespace TacticalDisplay.App.ViewModels;

public sealed record AarPendingRequestDisplay(string RequestId, string Callsign, string StatusText,
    string Source, string RequestMode, double? RequestedKg)
{
    public string DisplayText => $"{Callsign} · {Source} · {RequestMode}" +
        (RequestedKg is { } amount ? $" · {amount:0} kg" : string.Empty);
}

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
    private AarFuelSummaryState? _fuelSummary;
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
        _client.StateChanged += OnClientStateChanged;

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
        MoveAarQueueEntryTopCommand = Command(() => _ = MoveQueueEntryToAsync(0));
        MoveAarQueueEntryBottomCommand = Command(() => _ = MoveQueueEntryToAsync(_pendingRequests.Count - 1));
        SetAarPlannedOnloadCommand = Command(() => _ = SetPlanAsync());
        ToggleAarTransferModeCommand = Command(() => _ = ToggleModeAsync());
        ClearAarAsternCommand = Command(() => _ = SendOperationCommandAsync("CLEAR_ASTERN"), () => CanRunOperationCommand("CLEAR_ASTERN"));
        HoldAarOperationCommand = Command(() => _ = SendOperationCommandAsync("HOLD"), () => CanRunOperationCommand("HOLD"));
        StartAarTransferCommand = Command(() => _ = SendOperationCommandAsync("START_TRANSFER"), () => CanRunOperationCommand("START_TRANSFER"));
        StopAarTransferCommand = Command(() => _ = SendOperationCommandAsync("STOP_TRANSFER"), () => CanRunOperationCommand("STOP_TRANSFER"));
        ClearAarContactCommand = Command(() => _ = SendOperationCommandAsync("CLEAR_CONTACT"), () => CanRunOperationCommand("CLEAR_CONTACT"));
        DisconnectAarOperationCommand = Command(() => _ = SendOperationCommandAsync("DISCONNECT"), () => CanRunOperationCommand("DISCONNECT"));
        BreakawayAarOperationCommand = Command(() => _ = SendOperationCommandAsync("BREAKAWAY"), () => CanRunOperationCommand("BREAKAWAY"));
        ReconcileAarOperationCommand = Command(() => _ = SendOperationCommandAsync("RECONCILE"), () => CanRunOperationCommand("RECONCILE"));
        ToggleAarAvailabilityCommand = Command(() => _ = ToggleAvailabilityAsync());
        ToggleTankerAvailabilityCommand = Command(() => _ = ToggleTankerModeAsync());
        ApplyClientState(_client.State);
    }

    private RelayCommand Command(Action action, Func<bool>? canExecute = null) => new(action, canExecute);
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
    public RelayCommand MoveAarQueueEntryTopCommand { get; }
    public RelayCommand MoveAarQueueEntryBottomCommand { get; }
    public RelayCommand SetAarPlannedOnloadCommand { get; }
    public RelayCommand ToggleAarTransferModeCommand { get; }
    public RelayCommand ClearAarAsternCommand { get; }
    public RelayCommand HoldAarOperationCommand { get; }
    public RelayCommand StartAarTransferCommand { get; }
    public RelayCommand StopAarTransferCommand { get; }
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
    public string AarActiveOperationSummaryText => BuildActiveOperationSummary(_client.State.ActiveOperation);
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
    public bool CanToggleTankerMode => CanOfferTanker;
    public string TankerJoinStatusText => !_isConnected()
        ? "Connect to TacticalLink to join as tanker."
        : !_isTankerCapable()
            ? "Tanker role is not enabled for the active aircraft in the AAR registry."
            : _client.IsTankerJoined
                ? "Joined as tanker. Set availability to accept requests."
                : "Tanker role available for this aircraft.";
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
            Raise(nameof(AarActiveOperationSummaryText));
        }
    }

    public void OnPose(OwnshipState sample) => ObservePublish(_client.PublishPoseAsync(sample, _token), "pose");

    public void OnFuel(AarFuelReading reading)
    {
        if (!_isConnected() || (!_isTankerCapable() && !_isReceiverCapable())) return;
        var adapter = _fuelAdapter();
        var ready = adapter is { IsAvailable: true, CanReadFuel: true, CanWriteFuel: true };
        var watermark = _activeOperationId is { } id ? _fuelCoordinator.GetWatermark(id) : 0;
        ObservePublish(_client.PublishFuelStatusAsync(reading.CurrentFuelKg, reading.CapacityKg, ready, _activeOperationId, watermark, _token), "fuel-status");
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
        Raise(nameof(CanOfferTanker)); Raise(nameof(CanToggleTankerMode)); Raise(nameof(TankerJoinStatusText));
        Raise(nameof(IsAarReceiverCapable)); Raise(nameof(IsTankerCapable));
        Raise(nameof(IsLocalTankerJoined)); Raise(nameof(TankerButtonText)); Raise(nameof(AarAvailabilityButtonText));
        RaiseOperationCommandStates();
    }

    public void SetFuelAdapterUnavailable() => AarTankerMetricsText = "Fuel adapter unavailable";

    public void Dispose() => _client.StateChanged -= OnClientStateChanged;

    private string FormatFuelSummary(AarFuelSummaryState? fuel)
    {
        if (fuel?.CurrentFuelKg is not { } current || fuel.ProtectedReserveKg is not { } reserve || fuel.AvailableToPromiseKg is not { } available)
            return "Fuel adapter unavailable";
        return $"Fuel {FormatAmount(current)} {_unit} · reserve {FormatAmount(reserve)} {_unit} · available {FormatAmount(available)} {_unit}";
    }

    private string FormatMetrics(double transferred, double remaining, double flow, bool isRefueling) =>
        isRefueling && flow > 0
            ? $"Transferred {FormatAmount(transferred)} {_unit} · remaining {FormatAmount(remaining)} {_unit} · {FormatFlow(flow)} {_unit}/s · ETA {TimeSpan.FromSeconds(remaining / flow):mm\\:ss}"
            : $"Transferred {FormatAmount(transferred)} {_unit} · remaining {FormatAmount(remaining)} {_unit} · transfer paused";
    private string BuildActiveOperationSummary(AarOperationState? operation)
    {
        if (operation is null || !_activeIsLocalTanker) return string.Empty;
        var receiver = _peers().FirstOrDefault(peer => peer.ParticipantId == operation.ReceiverParticipantId);
        var callsign = receiver?.Callsign ?? operation.ReceiverParticipantId ?? "Receiver";
        var aircraft = receiver?.AircraftType ?? "Unknown aircraft";
        var requested = operation.RequestMode switch
        {
            "None" => "None (tanker added)",
            "Full" => "FULL",
            _ when operation.RequestedKg is { } amount => $"{FormatAmount(amount):0.##} {_unit}",
            _ => "Unknown"
        };
        var planned = operation.TransferMode == global::TacticalDisplay.App.TacticalLink.AarTransferMode.DryHookup
            ? "0 (Dry Hookup)"
            : $"{FormatAmount(operation.PlannedKg):0.##} {_unit}";
        return $"{callsign} · {aircraft} | Requested {requested} · Planned {planned} · Transferred {FormatAmount(operation.TransferredKg):0.##} {_unit} · Remaining {FormatAmount(operation.RemainingKg):0.##} {_unit} · {operation.TransferMode} · Fuel {(operation.FuelOnAuthorized ? "ON" : "OFF")}";
    }
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
        await _client.RequestFullRefuelAsync(SelectedAarTanker.ParticipantId, AarTransferMode, _token);
        AarStatusText = "FULL request sent";
    }
    private async Task RequestAsync()
    {
        if (!IsAarReceiverCapable || SelectedAarTanker is null) return;
        if (AarTransferMode == "DryHookup")
        {
            await _client.RequestFullRefuelAsync(SelectedAarTanker.ParticipantId, AarTransferMode, _token);
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
    private async Task MoveQueueEntryToAsync(int position)
    {
        if (SelectedAarRequest is null || _pendingRequests.Count == 0) return;
        await _client.MoveQueueEntryAsync(SelectedAarRequest.RequestId, Math.Clamp(position, 0, _pendingRequests.Count - 1), _token);
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

    private bool CanRunOperationCommand(string kind)
    {
        if (!_isConnected() || _activeOperationId is null || _client.State.ActiveOperation is not { } operation)
            return false;
        var tanker = operation.TankerParticipantId == _link.LocalParticipantId;
        var phase = operation.Phase;
        return kind switch
        {
            "CLEAR_ASTERN" => tanker && phase == AarOperationPhase.Accepted,
            "CLEAR_CONTACT" => tanker && phase == AarOperationPhase.Astern,
            "HOLD" => tanker && phase is AarOperationPhase.Astern or AarOperationPhase.ClearedContact or AarOperationPhase.Contact or AarOperationPhase.Refueling,
            "START_TRANSFER" => tanker && phase == AarOperationPhase.Contact && operation.TransferMode == global::TacticalDisplay.App.TacticalLink.AarTransferMode.Fuel &&
                _fuelAdapter() is { IsAvailable: true, CanReadFuel: true, CanWriteFuel: true },
            "STOP_TRANSFER" => tanker && phase == AarOperationPhase.Refueling,
            "DISCONNECT" => phase is AarOperationPhase.Accepted or AarOperationPhase.Astern or AarOperationPhase.ClearedContact or
                AarOperationPhase.Contact or AarOperationPhase.Refueling or AarOperationPhase.Suspended,
            "BREAKAWAY" => !AarState.IsTerminal(phase) && phase != AarOperationPhase.Unknown,
            "RECONCILE" => phase == AarOperationPhase.Suspended,
            _ => false
        };
    }

    private void RaiseOperationCommandStates()
    {
        ClearAarAsternCommand.RaiseCanExecuteChanged();
        HoldAarOperationCommand.RaiseCanExecuteChanged();
        StartAarTransferCommand.RaiseCanExecuteChanged();
        StopAarTransferCommand.RaiseCanExecuteChanged();
        ClearAarContactCommand.RaiseCanExecuteChanged();
        DisconnectAarOperationCommand.RaiseCanExecuteChanged();
        BreakawayAarOperationCommand.RaiseCanExecuteChanged();
        ReconcileAarOperationCommand.RaiseCanExecuteChanged();
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
        if (accept) await _client.AcceptRequestAsync(SelectedAarRequest.RequestId, AarTransferMode, plan, _token);
        else await _client.RejectRequestAsync(SelectedAarRequest.RequestId, _token);
        AarStatusText = accept ? "Request accepted" : "Request rejected";
        if (!accept) _pendingRequests.Remove(SelectedAarRequest);
        SelectedAarRequest = null;
    }

    private void OnClientStateChanged(AarState state)
    {
        if (!Application.Current.Dispatcher.CheckAccess())
        {
            Application.Current.Dispatcher.BeginInvoke(() => ApplyClientState(state));
            return;
        }
        ApplyClientState(state);
    }

    private void ApplyClientState(AarState state)
    {
        _activeOperations.Clear();
        foreach (var operation in state.Operations.Values.Where(operation => !AarState.IsTerminal(operation.Phase)))
            _activeOperations.Add(operation.OperationId);
        var active = state.ActiveOperation;
        _activeOperationId = active?.OperationId;
        _activeIsLocalTanker = active?.TankerParticipantId == _link.LocalParticipantId;
        if (active is not null) AarOperationStateText = active.Phase.ToString().ToUpperInvariant();
        else if (state.LastEventKind is "OPERATION_COMPLETE" or "OPERATION_FAILED" or "BREAKAWAY" or "OPERATION_CANCELLED")
            AarOperationStateText = state.Operations.Values.OrderByDescending(operation => operation.Revision).FirstOrDefault()?.Phase.ToString().ToUpperInvariant() ?? "No active operation";

        _transferMode = state.TransferMode == global::TacticalDisplay.App.TacticalLink.AarTransferMode.DryHookup ? "DryHookup" : "Fuel";
        _protectedReserveKg = state.FuelSummary?.ProtectedReserveKg ?? _protectedReserveKg;
        _ownPendingRequestId = state.OwnPendingRequestId;
        _committedNextOperationId = state.CommittedNext?.OperationId;
        _pendingRequests.Clear();
        foreach (var request in state.PendingQueue)
        {
            var callsign = _link.NearbyPeers.FirstOrDefault(peer => peer.ParticipantId == request.ReceiverParticipantId)?.Callsign ?? "Receiver";
            _pendingRequests.Add(new AarPendingRequestDisplay(request.RequestId, callsign, request.Status,
                request.Source == "TankerAdded" ? "Tanker added" : "Receiver request", request.RequestMode,
                request.RequestedKg));
        }

        AarStatusText = state.LastEventKind switch
        {
            "TANKER_MODE_JOINED" => "Tanker joined; set reserve and check adapter readiness",
            "TANKER_MODE_LEFT" => "Tanker mode off",
            "RESERVE_UPDATED" => "Protected reserve updated",
            "TANKER_AVAILABILITY_UPDATED" => state.TankerAvailability == "Available" ? "Tanker available for requests" : "Tanker unavailable",
            "MODULE_ERROR" => state.MaxAllowedKg is { } maximum
                ? $"Plan exceeds the safe maximum of {FormatAmount(maximum):0.##} {_unit}."
                : state.LastError ?? "AAR command failed",
            "OPERATION_SUSPENDED" => "AAR stopped; reconnect and reconcile before continuing",
            "OPERATION_FAILED" => "AAR operation failed",
            { } kind => kind.Replace('_', ' '),
            _ => AarStatusText
        };
        if (_isTankerCapable())
        {
            _fuelSummary = state.FuelSummary;
            if (state.FuelSummary is not null) AarTankerMetricsText = FormatFuelSummary(_fuelSummary);
            if (active is { } op && op.Phase == AarOperationPhase.Refueling && op.EffectiveFlowKgPerSecond is > 0)
            {
                _transferMetrics = (op.TransferredKg, op.RemainingKg, op.EffectiveFlowKgPerSecond.Value, true);
                AarTankerMetricsText = FormatMetrics(op.TransferredKg, op.RemainingKg, op.EffectiveFlowKgPerSecond.Value, true);
            }
            else if (active is { } stopped && stopped.Phase is AarOperationPhase.Contact or AarOperationPhase.Astern)
            {
                _transferMetrics = (stopped.TransferredKg, stopped.RemainingKg, 0, false);
                AarTankerMetricsText = FormatMetrics(stopped.TransferredKg, stopped.RemainingKg, 0, false);
            }
        }

        Raise(nameof(AarTransferMode));
        Raise(nameof(AvailableAarTankers)); Raise(nameof(AvailableAarReceivers)); Raise(nameof(HasOwnAarPendingRequest));
        Raise(nameof(HasAarCommittedNext)); Raise(nameof(HasAarOperation)); Raise(nameof(IsLocalTankerForActiveOperation));
        Raise(nameof(IsLocalTankerJoined)); Raise(nameof(AarAvailabilityButtonText)); Raise(nameof(TankerButtonText));
        Raise(nameof(TankerJoinStatusText));
        Raise(nameof(AarActiveOperationSummaryText));
        Raise(nameof(HasActiveOperations));
        RaiseOperationCommandStates();
        _stateChanged();
    }
}

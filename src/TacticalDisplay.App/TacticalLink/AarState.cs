namespace TacticalDisplay.App.TacticalLink;

internal enum AarOperationPhase
{
    Unknown,
    Accepted,
    Astern,
    ClearedContact,
    Contact,
    Refueling,
    Suspended,
    Disconnecting,
    Complete,
    Failed,
    Breakaway,
    Cancelled
}

internal enum AarTransferMode
{
    Fuel,
    DryHookup
}

internal sealed record AarOperationState(
    string OperationId,
    string? RequestId,
    string RequestMode,
    string Source,
    double? RequestedKg,
    string? TankerParticipantId,
    string? ReceiverParticipantId,
    string Slot,
    AarOperationPhase Phase,
    long Revision,
    double PlannedKg,
    double TransferredKg,
    double RemainingKg,
    double? EffectiveFlowKgPerSecond,
    AarTransferMode TransferMode,
    bool FuelOnAuthorized);

internal sealed record AarQueueEntryState(
    string RequestId,
    string? ReceiverParticipantId,
    string? TankerParticipantId,
    string Source,
    string RequestMode,
    double? RequestedKg,
    int QueueOrder,
    string Status,
    string? OperationId);

internal sealed record AarFuelSummaryState(
    double? CurrentFuelKg,
    double? ProtectedReserveKg,
    double? CommittedFuelKg,
    double? AvailableToPromiseKg);

internal sealed record AarState(
    bool TankerJoined,
    string TankerAvailability,
    AarFuelSummaryState? FuelSummary,
    IReadOnlyList<AarQueueEntryState> PendingQueue,
    AarOperationState? CommittedNext,
    IReadOnlyDictionary<string, AarOperationState> Operations,
    string? OwnPendingRequestId,
    AarTransferMode TransferMode,
    string? LastEventKind,
    string? LastError,
    double? MaxAllowedKg)
{
    public static AarState Empty { get; } = new(false, "Off", null, [], null,
        new Dictionary<string, AarOperationState>(StringComparer.Ordinal), null, AarTransferMode.Fuel, null, null, null);

    public AarOperationState? ActiveOperation => Operations.Values.FirstOrDefault(operation => operation.Slot == "Active" && !IsTerminal(operation.Phase));

    public static AarOperationPhase ParsePhase(string? value) => value switch
    {
        "Accepted" => AarOperationPhase.Accepted,
        "Astern" => AarOperationPhase.Astern,
        "ClearedContact" => AarOperationPhase.ClearedContact,
        "Contact" => AarOperationPhase.Contact,
        "Refueling" => AarOperationPhase.Refueling,
        "Suspended" => AarOperationPhase.Suspended,
        "Disconnecting" => AarOperationPhase.Disconnecting,
        "Complete" => AarOperationPhase.Complete,
        "Failed" => AarOperationPhase.Failed,
        "Breakaway" => AarOperationPhase.Breakaway,
        "Cancelled" => AarOperationPhase.Cancelled,
        _ => AarOperationPhase.Unknown
    };

    public static bool IsTerminal(AarOperationPhase phase) => phase is AarOperationPhase.Complete or AarOperationPhase.Failed or AarOperationPhase.Breakaway or AarOperationPhase.Cancelled;
}

namespace TacticalDisplay.App.Data;

public enum AarFuelApplyStatus { Success, Partial, Failed }

public sealed record AarFuelReading(double CurrentFuelKg, double CapacityKg, DateTimeOffset SampledAtUtc);

public sealed record AarFuelApplyResult(double RequestedKg, double AppliedKg, AarFuelApplyStatus Status, string? Error = null);

public interface IAarFuelAdapter
{
    event EventHandler<AarFuelReading>? FuelSampled;
    bool IsAvailable { get; }
    bool CanReadFuel { get; }
    bool CanWriteFuel { get; }
    AarFuelReading? ReadFuel();
    Task<AarFuelApplyResult> ApplyFuelDeltaKgAsync(double deltaKg, CancellationToken cancellationToken);
}

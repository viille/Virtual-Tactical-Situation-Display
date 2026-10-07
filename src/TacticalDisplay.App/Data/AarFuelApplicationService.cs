namespace TacticalDisplay.App.Data;

public sealed class AarFuelApplicationService(IAarFuelAdapter adapter, TimeProvider? timeProvider = null)
{
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;

    public async Task<AarFuelApplyResult> ApplyDeltaKgAsync(double deltaKg, double protectedReserveKg, CancellationToken cancellationToken)
    {
        if (!double.IsFinite(deltaKg) || deltaKg == 0 || !double.IsFinite(protectedReserveKg) || protectedReserveKg < 0)
            return new AarFuelApplyResult(deltaKg, 0, AarFuelApplyStatus.Failed, "Fuel delta or reserve is invalid.");
        if (!adapter.IsAvailable || !adapter.CanReadFuel || !adapter.CanWriteFuel)
            return new AarFuelApplyResult(deltaKg, 0, AarFuelApplyStatus.Failed, "Fuel adapter is unavailable or read-only.");

        var reading = adapter.ReadFuel();
        if (reading is null || _clock.GetUtcNow() - reading.SampledAtUtc > TimeSpan.FromSeconds(5) ||
            !double.IsFinite(reading.CurrentFuelKg) || !double.IsFinite(reading.CapacityKg) ||
            reading.CurrentFuelKg < 0 || reading.CapacityKg <= 0 || reading.CurrentFuelKg > reading.CapacityKg + 0.01)
            return new AarFuelApplyResult(deltaKg, 0, AarFuelApplyStatus.Failed, "Fuel reading is missing, stale, or outside capacity.");

        var availableKg = deltaKg > 0
            ? Math.Max(0, reading.CapacityKg - reading.CurrentFuelKg)
            : Math.Max(0, reading.CurrentFuelKg - protectedReserveKg);
        var boundedDelta = Math.Sign(deltaKg) * Math.Min(Math.Abs(deltaKg), availableKg);
        if (Math.Abs(boundedDelta) < 0.001)
            return new AarFuelApplyResult(deltaKg, 0, AarFuelApplyStatus.Failed, "Fuel capacity or protected reserve boundary reached.");

        AarFuelApplyResult result;
        try { result = await adapter.ApplyFuelDeltaKgAsync(boundedDelta, cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex) { return new AarFuelApplyResult(deltaKg, 0, AarFuelApplyStatus.Failed, ex.Message); }

        if (!double.IsFinite(result.AppliedKg))
            return new AarFuelApplyResult(deltaKg, 0, AarFuelApplyStatus.Failed, "Adapter returned a non-finite applied amount.");
        if (result.AppliedKg != 0 && Math.Sign(result.AppliedKg) != Math.Sign(deltaKg))
            return new AarFuelApplyResult(deltaKg, 0, AarFuelApplyStatus.Failed, "Adapter reported fuel applied in the wrong direction.");

        var actual = result.AppliedKg;
        var status = Math.Abs(actual) < 0.001
            ? AarFuelApplyStatus.Failed
            : result.Status == AarFuelApplyStatus.Failed
                ? AarFuelApplyStatus.Partial
                : Math.Abs(actual - deltaKg) <= 0.01
                    ? AarFuelApplyStatus.Success
                    : AarFuelApplyStatus.Partial;
        return new AarFuelApplyResult(deltaKg, actual, status, result.Error);
    }
}

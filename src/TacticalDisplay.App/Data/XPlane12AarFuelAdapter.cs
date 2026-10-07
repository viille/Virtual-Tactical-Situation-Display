using System.IO;
using System.Text.Json;
using TacticalDisplay.App.Services;

namespace TacticalDisplay.App.Data;

internal sealed class XPlane12AarFuelAdapter : IAarFuelAdapter
{
    private const string FuelMassDataRef = "sim/flightmodel/weight/m_fuel";
    private const string TotalFuelDataRef = "sim/flightmodel/weight/m_fuel_total";
    private const string TotalCapacityDataRef = "sim/aircraft/weight/acf_m_fuel_tot";
    private const string TankRatioDataRef = "sim/aircraft/overflow/acf_tank_rat";
    private const double PoundsToKilograms = 0.45359237;
    private static readonly TimeSpan FuelFreshness = TimeSpan.FromSeconds(3);

    private readonly XPlane12WebApiClient _webApi;
    private readonly Func<bool> _isConnected;
    private readonly TimeProvider _clock;
    private readonly Dictionary<string, long> _ids = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    private double[]? _tankCapacitiesKg;
    private AarFuelReading? _latestFuel;
    private bool _writeProbePassed;

    public XPlane12AarFuelAdapter(XPlane12WebApiTrafficFeed feed, TimeProvider? timeProvider = null)
        : this(feed.WebApi, () => feed.IsConnected, timeProvider)
    {
    }

    internal XPlane12AarFuelAdapter(XPlane12WebApiClient webApi, Func<bool> isConnected, TimeProvider? timeProvider = null)
    {
        _webApi = webApi;
        _isConnected = isConnected;
        _clock = timeProvider ?? TimeProvider.System;
    }

    public event EventHandler<AarFuelReading>? FuelSampled;
    public bool IsAvailable => _isConnected();
    public bool CanReadFuel => IsAvailable && ReadFuel() is not null;
    public bool CanWriteFuel => CanReadFuel && _writeProbePassed && _tankCapacitiesKg is not null;

    public AarFuelReading? ReadFuel()
    {
        lock (_gate)
        {
            if (_latestFuel is not { } reading || !IsValidReading(reading) ||
                _clock.GetUtcNow() - reading.SampledAtUtc > FuelFreshness || reading.SampledAtUtc - _clock.GetUtcNow() > TimeSpan.FromSeconds(1))
                return null;
            return reading;
        }
    }

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        _ids.Clear();
        _tankCapacitiesKg = null;
        _writeProbePassed = false;
        foreach (var name in new[] { FuelMassDataRef, TotalFuelDataRef, TotalCapacityDataRef, TankRatioDataRef })
            _ids[name] = await _webApi.ResolveDataRefIdAsync(name, cancellationToken).ConfigureAwait(false);

        var tankFuel = await ReadArrayAsync(FuelMassDataRef, cancellationToken).ConfigureAwait(false);
        var totalFuel = await ReadScalarAsync(TotalFuelDataRef, cancellationToken).ConfigureAwait(false);
        var totalCapacityPounds = await ReadScalarAsync(TotalCapacityDataRef, cancellationToken).ConfigureAwait(false);
        var tankRatios = await ReadArrayAsync(TankRatioDataRef, cancellationToken).ConfigureAwait(false);
        var capacities = BuildTankCapacities(totalCapacityPounds * PoundsToKilograms, tankRatios, tankFuel);
        if (!double.IsFinite(totalFuel) || Math.Abs(totalFuel - tankFuel.Sum()) > 0.5)
            throw new InvalidDataException("XP12 fuel tank mass array does not agree with the simulator aggregate fuel mass.");

        lock (_gate) _tankCapacitiesKg = capacities;
        UpdateFuelReading(tankFuel, capacities);
        DataSourceDebugLog.Info("XPlane12", $"AAR fuel discovery | tanks={tankFuel.Length} currentKg={tankFuel.Sum():0.00} capacityKg={capacities.Sum():0.00} rawCapacity={totalCapacityPounds:0.00} rawTankRatios={string.Join(",", tankRatios.Select(value => value.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture)))}");

        try
        {
            await _webApi.PatchValueAsync(_ids[FuelMassDataRef], tankFuel, cancellationToken).ConfigureAwait(false);
            var readBack = await ReadArrayAsync(FuelMassDataRef, cancellationToken).ConfigureAwait(false);
            if (ArraysMatch(tankFuel, readBack) && ValidateTankArray(readBack, capacities))
            {
                _writeProbePassed = true;
                DataSourceDebugLog.Info("XPlane12", $"AAR fuel write probe | result=passed tanks={readBack.Length} aggregateKg={readBack.Sum():0.00}");
            }
            else
                DataSourceDebugLog.Warn("XPlane12", $"AAR fuel write probe failed | result=readback-mismatch requestedTanks={tankFuel.Length} readbackTanks={readBack.Length} XP12 remains read-only");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            DataSourceDebugLog.Warn("XPlane12", $"AAR fuel write probe failed | result=exception XP12 remains read-only | {ex.Message}");
        }
    }

    public async Task RefreshFuelAsync(CancellationToken cancellationToken)
    {
        if (!_ids.ContainsKey(FuelMassDataRef) || _tankCapacitiesKg is not { } capacities) return;
        var tanks = await ReadArrayAsync(FuelMassDataRef, cancellationToken).ConfigureAwait(false);
        var aggregate = await ReadScalarAsync(TotalFuelDataRef, cancellationToken).ConfigureAwait(false);
        if (!ValidateTankArray(tanks, capacities) || !double.IsFinite(aggregate) || Math.Abs(aggregate - tanks.Sum()) > 0.5)
        {
            lock (_gate) _latestFuel = null;
            _writeProbePassed = false;
            throw new InvalidDataException("XP12 fuel state is invalid or the aircraft has replaced the standard tank values.");
        }
        UpdateFuelReading(tanks, capacities);
    }

    public async Task<AarFuelApplyResult> ApplyFuelDeltaKgAsync(double deltaKg, CancellationToken cancellationToken)
    {
        if (!double.IsFinite(deltaKg) || deltaKg == 0)
            return new AarFuelApplyResult(deltaKg, 0, AarFuelApplyStatus.Failed, "Fuel delta must be finite and non-zero.");
        if (!CanWriteFuel || _tankCapacitiesKg is not { } capacities)
            return new AarFuelApplyResult(deltaKg, 0, AarFuelApplyStatus.Failed, "XP12 has no current verified writable fuel state.");

        try
        {
            DataSourceDebugLog.Debug("XPlane12", $"AAR fuel mutation | requestedKg={deltaKg:0.00}");
            var before = await ReadArrayAsync(FuelMassDataRef, cancellationToken).ConfigureAwait(false);
            var beforeAggregate = await ReadScalarAsync(TotalFuelDataRef, cancellationToken).ConfigureAwait(false);
            if (!ValidateTankArray(before, capacities) || !double.IsFinite(beforeAggregate) || Math.Abs(beforeAggregate - before.Sum()) > 0.5)
            {
                _writeProbePassed = false;
                lock (_gate) _latestFuel = null;
                return new AarFuelApplyResult(deltaKg, 0, AarFuelApplyStatus.Failed, "XP12 fuel read is outside known tank limits or disagrees with the aggregate.");
            }
            var target = DistributeDelta(before, capacities, deltaKg);
            if (ArraysMatch(before, target)) return new AarFuelApplyResult(deltaKg, 0, AarFuelApplyStatus.Failed, "XP12 fuel capacity or empty boundary reached.");

            await _webApi.PatchValueAsync(_ids[FuelMassDataRef], target, cancellationToken).ConfigureAwait(false);
            var after = await ReadArrayAsync(FuelMassDataRef, cancellationToken).ConfigureAwait(false);
            if (!ValidateTankArray(after, capacities))
            {
                _writeProbePassed = false;
                lock (_gate) _latestFuel = null;
                return new AarFuelApplyResult(deltaKg, 0, AarFuelApplyStatus.Failed, "XP12 rewrote fuel values outside the supported tank limits.");
            }

            var applied = after.Sum() - before.Sum();
            var aggregate = await ReadScalarAsync(TotalFuelDataRef, cancellationToken).ConfigureAwait(false);
            if (!double.IsFinite(applied) || Math.Sign(applied) != Math.Sign(deltaKg) || Math.Abs(applied) > Math.Abs(deltaKg) + 0.05 ||
                !double.IsFinite(aggregate) || Math.Abs(aggregate - after.Sum()) > 0.5)
            {
                _writeProbePassed = false;
                lock (_gate) _latestFuel = null;
                return new AarFuelApplyResult(deltaKg, 0, AarFuelApplyStatus.Failed, "XP12 post-write aggregate read-back did not match the tank values.");
            }

            UpdateFuelReading(after, capacities);
            var status = Math.Abs(applied) < 0.001 ? AarFuelApplyStatus.Failed :
                Math.Abs(applied - deltaKg) <= 0.05 ? AarFuelApplyStatus.Success : AarFuelApplyStatus.Partial;
            if (status == AarFuelApplyStatus.Failed) _writeProbePassed = false;
            DataSourceDebugLog.Info("XPlane12", $"AAR fuel mutation | requestedKg={deltaKg:0.00} beforeKg={before.Sum():0.00} afterKg={after.Sum():0.00} appliedKg={applied:0.00} status={status}");
            return new AarFuelApplyResult(deltaKg, applied, status, status == AarFuelApplyStatus.Partial ? "XP12 applied a bounded partial fuel change." : null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            _writeProbePassed = false;
            DataSourceDebugLog.Error("XPlane12", $"AAR fuel mutation failed | requestedKg={deltaKg:0.00}", ex);
            return new AarFuelApplyResult(deltaKg, 0, AarFuelApplyStatus.Failed, ex.Message);
        }
    }

    public void MarkUnavailable()
    {
        lock (_gate) _latestFuel = null;
        _writeProbePassed = false;
    }

    private async Task<double[]> ReadArrayAsync(string name, CancellationToken cancellationToken)
    {
        using var document = await _webApi.GetValueDocumentAsync(_ids[name], cancellationToken).ConfigureAwait(false);
        if (!document.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array) return [];
        var values = new double[data.GetArrayLength()];
        var index = 0;
        foreach (var item in data.EnumerateArray()) values[index++] = ReadNumber(item);
        return values;
    }

    private async Task<double> ReadScalarAsync(string name, CancellationToken cancellationToken)
    {
        using var document = await _webApi.GetValueDocumentAsync(_ids[name], cancellationToken).ConfigureAwait(false);
        return document.RootElement.TryGetProperty("data", out var data) ? ReadNumber(data) : double.NaN;
    }

    private void UpdateFuelReading(double[] tanks, double[] capacities)
    {
        var reading = new AarFuelReading(tanks.Sum(), capacities.Sum(), _clock.GetUtcNow());
        lock (_gate) _latestFuel = reading;
        FuelSampled?.Invoke(this, reading);
    }

    private static double[] BuildTankCapacities(double totalCapacityKg, double[] ratios, double[] current)
    {
        if (!double.IsFinite(totalCapacityKg) || totalCapacityKg <= 0 || ratios.Length != 9 || current.Length != 9 ||
            ratios.Any(value => !double.IsFinite(value) || value < 0) || current.Any(value => !double.IsFinite(value) || value < 0))
            throw new InvalidDataException("XP12 fuel capacity or tank ratio DataRefs are unavailable or invalid.");
        var ratioSum = ratios.Sum();
        if (!double.IsFinite(ratioSum) || ratioSum <= 0) throw new InvalidDataException("XP12 tank capacity ratios are invalid.");
        var capacities = ratios.Select(value => totalCapacityKg * value / ratioSum).ToArray();
        if (!ValidateTankArray(current, capacities)) throw new InvalidDataException("XP12 fuel mass exceeds the calculated tank capacity.");
        return capacities;
    }

    private static double[] DistributeDelta(double[] current, double[] capacities, double deltaKg)
    {
        var result = (double[])current.Clone();
        var amount = Math.Min(Math.Abs(deltaKg), Enumerable.Range(0, result.Length)
            .Sum(index => deltaKg > 0 ? capacities[index] - result[index] : result[index]));
        var available = Enumerable.Range(0, result.Length)
            .Select(index => deltaKg > 0 ? capacities[index] - result[index] : result[index]).ToArray();
        var total = available.Sum();
        for (var index = 0; index < result.Length; index++)
        {
            if (available[index] <= 0 || total <= 0) continue;
            result[index] += Math.Sign(deltaKg) * Math.Min(available[index], amount * available[index] / total);
        }
        return result;
    }

    private static bool ValidateTankArray(double[] tanks, double[] capacities) => tanks.Length == capacities.Length &&
        tanks.Select((value, index) => double.IsFinite(value) && value >= 0 && value <= capacities[index] + 0.05).All(valid => valid);

    private static bool ArraysMatch(double[] left, double[] right) => left.Length == right.Length &&
        left.Zip(right).All(pair => Math.Abs(pair.First - pair.Second) <= 0.01);

    private static bool IsValidReading(AarFuelReading reading) => double.IsFinite(reading.CurrentFuelKg) &&
        double.IsFinite(reading.CapacityKg) && reading.CurrentFuelKg >= 0 && reading.CapacityKg > 0 && reading.CurrentFuelKg <= reading.CapacityKg + 0.05;

    private static double ReadNumber(JsonElement value) => value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number) ? number : double.NaN;
}

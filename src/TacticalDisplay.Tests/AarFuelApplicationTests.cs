using TacticalDisplay.App.Data;
using Xunit;

namespace TacticalDisplay.Tests;

public sealed class AarFuelApplicationTests
{
    [Fact]
    public async Task SuccessfulDeltaReportsActualAppliedMass()
    {
        var adapter = new FakeAdapter(100, 200, requested => new AarFuelApplyResult(requested, requested, AarFuelApplyStatus.Success));

        var result = await new AarFuelApplicationService(adapter).ApplyDeltaKgAsync(5, 0, CancellationToken.None);

        Assert.Equal(AarFuelApplyStatus.Success, result.Status);
        Assert.Equal(5, result.RequestedKg);
        Assert.Equal(5, result.AppliedKg);
    }

    [Fact]
    public async Task PartialDeltaReportsOnlySimulatorAppliedMass()
    {
        var adapter = new FakeAdapter(100, 200, requested => new AarFuelApplyResult(requested, requested * 0.6, AarFuelApplyStatus.Partial));

        var result = await new AarFuelApplicationService(adapter).ApplyDeltaKgAsync(5, 0, CancellationToken.None);

        Assert.Equal(AarFuelApplyStatus.Partial, result.Status);
        Assert.Equal(3, result.AppliedKg);
    }

    [Fact]
    public async Task FailedDeltaReportsZeroAppliedMass()
    {
        var adapter = new FakeAdapter(100, 200, requested => new AarFuelApplyResult(requested, 0, AarFuelApplyStatus.Failed));

        var result = await new AarFuelApplicationService(adapter).ApplyDeltaKgAsync(5, 0, CancellationToken.None);

        Assert.Equal(AarFuelApplyStatus.Failed, result.Status);
        Assert.Equal(0, result.AppliedKg);
    }

    [Fact]
    public async Task ReceiverCapacityClampsTheWriteAndReportsPartial()
    {
        var adapter = new FakeAdapter(98, 100, requested => new AarFuelApplyResult(requested, requested, AarFuelApplyStatus.Success));

        var result = await new AarFuelApplicationService(adapter).ApplyDeltaKgAsync(5, 0, CancellationToken.None);

        Assert.Equal(2, adapter.LastRequestedDeltaKg);
        Assert.Equal(2, result.AppliedKg);
        Assert.Equal(AarFuelApplyStatus.Partial, result.Status);
    }

    [Fact]
    public async Task TankerProtectedReserveClampsFuelRemoval()
    {
        var adapter = new FakeAdapter(50, 100, requested => new AarFuelApplyResult(requested, requested, AarFuelApplyStatus.Success));

        var result = await new AarFuelApplicationService(adapter).ApplyDeltaKgAsync(-5, 48, CancellationToken.None);

        Assert.Equal(-2, adapter.LastRequestedDeltaKg);
        Assert.Equal(-2, result.AppliedKg);
        Assert.Equal(AarFuelApplyStatus.Partial, result.Status);
    }

    [Fact]
    public async Task UnavailableOrReadOnlyAdapterFailsClosed()
    {
        var adapter = new FakeAdapter(100, 200, requested => new AarFuelApplyResult(requested, requested, AarFuelApplyStatus.Success)) { CanWriteFuel = false };

        var result = await new AarFuelApplicationService(adapter).ApplyDeltaKgAsync(5, 0, CancellationToken.None);

        Assert.Equal(AarFuelApplyStatus.Failed, result.Status);
        Assert.Null(adapter.LastRequestedDeltaKg);
    }

    [Fact]
    public async Task StaleFuelReadingFailsClosed()
    {
        var adapter = new FakeAdapter(100, 200, requested => new AarFuelApplyResult(requested, requested, AarFuelApplyStatus.Success))
        {
            SampledAtUtc = DateTimeOffset.UtcNow.AddSeconds(-10)
        };

        var result = await new AarFuelApplicationService(adapter).ApplyDeltaKgAsync(5, 0, CancellationToken.None);

        Assert.Equal(AarFuelApplyStatus.Failed, result.Status);
        Assert.Null(adapter.LastRequestedDeltaKg);
    }

    private sealed class FakeAdapter(double currentFuelKg, double capacityKg, Func<double, AarFuelApplyResult> apply) : IAarFuelAdapter
    {
        public event EventHandler<AarFuelReading>? FuelSampled { add { } remove { } }
        public bool IsAvailable { get; set; } = true;
        public bool CanReadFuel { get; set; } = true;
        public bool CanWriteFuel { get; set; } = true;
        public double? LastRequestedDeltaKg { get; private set; }
        public DateTimeOffset SampledAtUtc { get; set; } = DateTimeOffset.UtcNow;
        public AarFuelReading? ReadFuel() => new(currentFuelKg, capacityKg, SampledAtUtc);

        public Task<AarFuelApplyResult> ApplyFuelDeltaKgAsync(double deltaKg, CancellationToken cancellationToken)
        {
            LastRequestedDeltaKg = deltaKg;
            return Task.FromResult(apply(deltaKg));
        }
    }
}

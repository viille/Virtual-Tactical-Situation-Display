using TacticalDisplay.App.Data;
using Xunit;

namespace TacticalDisplay.Tests;

public sealed class AarFuelProposalProcessorTests
{
    [Fact]
    public async Task ReceiverProposalAddsFuelAndDuplicateDoesNotApplyTwice()
    {
        var processor = new AarFuelProposalProcessor();
        var adapter = new RecordingAdapter(100, 500);

        var first = await processor.ApplyAsync("proposal-1", "operation-1", "Receiver", 100, 100, 0, adapter, CancellationToken.None);
        var replay = await processor.ApplyAsync("proposal-1", "operation-1", "Receiver", 100, 100, 0, adapter, CancellationToken.None);

        Assert.NotNull(first);
        Assert.Equal(AarFuelApplyStatus.Success, first.Status);
        Assert.Equal(100, first.AppliedKg);
        Assert.Equal(100, first.AppliedCumulativeKg);
        Assert.Equal(first, replay);
        Assert.Equal(1, adapter.ApplyCount);
        Assert.Equal(200, adapter.CurrentFuelKg);
    }

    [Fact]
    public async Task TankerProposalRemovesFuelAndKeepsPositiveCumulativeMass()
    {
        var processor = new AarFuelProposalProcessor();
        var adapter = new RecordingAdapter(500, 700);

        var result = await processor.ApplyAsync("proposal-2", "operation-2", "Tanker", 100, 100, 50, adapter, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(AarFuelApplyStatus.Success, result.Status);
        Assert.Equal(100, result.AppliedKg);
        Assert.Equal(100, result.AppliedCumulativeKg);
        Assert.Equal(-100, adapter.LastDeltaKg);
        Assert.Equal(400, adapter.CurrentFuelKg);
    }

    [Fact]
    public async Task ProposalIdCannotBeReusedWithDifferentDelta()
    {
        var processor = new AarFuelProposalProcessor();
        var adapter = new RecordingAdapter(100, 500);
        await processor.ApplyAsync("proposal-3", "operation-3", "Receiver", 100, 100, 0, adapter, CancellationToken.None);

        await Assert.ThrowsAsync<InvalidOperationException>(() => processor.ApplyAsync(
            "proposal-3", "operation-3", "Receiver", 120, 120, 0, adapter, CancellationToken.None));

        Assert.Equal(1, adapter.ApplyCount);
        Assert.Equal(200, adapter.CurrentFuelKg);
    }

    private sealed class RecordingAdapter(double currentFuelKg, double capacityKg) : IAarFuelAdapter
    {
        public event EventHandler<AarFuelReading>? FuelSampled { add { } remove { } }
        public bool IsAvailable => true;
        public bool CanReadFuel => true;
        public bool CanWriteFuel => true;
        public int ApplyCount { get; private set; }
        public double CurrentFuelKg { get; private set; } = currentFuelKg;
        public double? LastDeltaKg { get; private set; }
        public AarFuelReading? ReadFuel() => new(CurrentFuelKg, capacityKg, DateTimeOffset.UtcNow);

        public Task<AarFuelApplyResult> ApplyFuelDeltaKgAsync(double deltaKg, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ApplyCount++;
            LastDeltaKg = deltaKg;
            CurrentFuelKg += deltaKg;
            return Task.FromResult(new AarFuelApplyResult(deltaKg, deltaKg, AarFuelApplyStatus.Success));
        }
    }
}

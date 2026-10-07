using TacticalDisplay.App.Data;
using TacticalDisplay.App.TacticalLink;
using TacticalDisplay.Core.Models;
using Xunit;

namespace TacticalDisplay.Tests;

public sealed class AarFuelTransferCoordinatorTests
{
    [Fact]
    public async Task BreakawayRevisionCancelsInFlightProposalAndRejectsLaterStaleProposal()
    {
        var transport = new TacticalLinkClient(null!);
        using var client = new AarClient(transport, CancellationToken.None);
        var adapter = new BlockingFuelAdapter();
        using var coordinator = new AarFuelTransferCoordinator(client, () => adapter, () => 0, CancellationToken.None);

        transport.HandleMessage(Event(1, "TRANSFER_PROPOSAL", 20,
            "\"proposalId\":\"p20\",\"participantRole\":\"Receiver\",\"deltaKg\":10,\"targetCumulativeKg\":10"));
        await adapter.ApplyStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        transport.HandleMessage(Event(2, "BREAKAWAY", 21, ""));
        await adapter.ApplyCancelled.Task.WaitAsync(TimeSpan.FromSeconds(2));

        transport.HandleMessage(Event(3, "TRANSFER_PROPOSAL", 20,
            "\"proposalId\":\"stale\",\"participantRole\":\"Receiver\",\"deltaKg\":10,\"targetCumulativeKg\":20"));
        await Task.Delay(50);

        Assert.Equal(1, adapter.ApplyCalls);
        Assert.Equal(100, adapter.CurrentFuelKg);
    }

    private static string Event(long sequence, string kind, long revision, string payloadFields) =>
        $"{{\"protocolVersion\":1,\"type\":\"MODULE_EVENT\",\"module\":\"aar\",\"moduleProtocolVersion\":1,\"transportSequence\":{sequence},\"kind\":\"{kind}\",\"operationId\":\"op\",\"operationRevision\":{revision},\"payload\":{{{payloadFields}}}}}";

    private sealed class BlockingFuelAdapter : IAarFuelAdapter
    {
        public event EventHandler<AarFuelReading>? FuelSampled { add { } remove { } }
        public bool IsAvailable => true;
        public bool CanReadFuel => true;
        public bool CanWriteFuel => true;
        public double CurrentFuelKg { get; private set; } = 100;
        public int ApplyCalls { get; private set; }
        public TaskCompletionSource ApplyStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ApplyCancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public AarFuelReading? ReadFuel() => new(CurrentFuelKg, 1000, DateTimeOffset.UtcNow);

        public async Task<AarFuelApplyResult> ApplyFuelDeltaKgAsync(double deltaKg, CancellationToken cancellationToken)
        {
            ApplyCalls++;
            ApplyStarted.TrySetResult();
            try { await Task.Delay(Timeout.Infinite, cancellationToken); }
            catch (OperationCanceledException)
            {
                ApplyCancelled.TrySetResult();
                throw;
            }
            CurrentFuelKg += deltaKg;
            return new AarFuelApplyResult(deltaKg, deltaKg, AarFuelApplyStatus.Success);
        }
    }
}

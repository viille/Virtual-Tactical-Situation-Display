using TacticalDisplay.App.Data;
using Xunit;

namespace TacticalDisplay.Tests;

public sealed class MsfsAarFuelAdapterTests
{
    [Fact]
    public async Task ApplyUsesMandatoryReadbackAsAppliedAmountEvenWhenBridgeReportsFailure()
    {
        var transport = new FakeTransport();
        var adapter = new MsfsAarFuelAdapter(transport);
        await adapter.ConnectAsync(CancellationToken.None);

        var result = await adapter.ApplyFuelDeltaKgAsync(50, CancellationToken.None);

        Assert.Equal(AarBridgeRuntimeState.ConnectedWritable, adapter.RuntimeState);
        Assert.Equal(3, result.AppliedKg, 2);
        Assert.Equal(AarFuelApplyStatus.Failed, result.Status);
        Assert.Equal(3, adapter.ReadFuel()!.CurrentFuelKg - 100);
    }

    [Fact]
    public async Task ProtocolMismatchFailsClosed()
    {
        var transport = new FakeTransport(protocolVersion: 2);
        var adapter = new MsfsAarFuelAdapter(transport);

        await adapter.ConnectAsync(CancellationToken.None);

        Assert.Equal(AarBridgeRuntimeState.ProtocolMismatch, adapter.RuntimeState);
        Assert.False(adapter.CanReadFuel);
        Assert.False(adapter.CanWriteFuel);
    }

    [Fact]
    public void TankDistributionIsProportionalAndClampedToCapacityOrFuel()
    {
        var tanks = new[]
        {
            new AarBridgeFuelTank { TankId = "left", CurrentKg = 50, CapacityKg = 100, Writable = true },
            new AarBridgeFuelTank { TankId = "right", CurrentKg = 150, CapacityKg = 200, Writable = true }
        };

        var positive = AarBridgeFuelDistributionPolicy.Distribute(tanks, 20);
        var negative = AarBridgeFuelDistributionPolicy.Distribute(tanks, -1000);

        Assert.Equal(new[] { 10d, 10d }, positive.Select(item => item.DeltaKg));
        Assert.Equal(new[] { -50d, -150d }, negative.Select(item => item.DeltaKg));
    }

    private sealed class FakeTransport(int protocolVersion = 1) : IAarBridgeTransport
    {
        private int _fuelKg = 100;
        public bool IsConnected => true;

        public Task<AarBridgeResponse> SendAsync(AarBridgeRequest request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var response = request.Action switch
            {
                "HELLO" => new AarBridgeResponse { RequestId = request.RequestId, Action = request.Action, ProtocolVersion = protocolVersion, BridgeVersion = "1.0.0", Status = "Success" },
                "GET_CAPABILITIES" => new AarBridgeResponse { RequestId = request.RequestId, Action = request.Action, ProtocolVersion = 1, Capabilities = ["fuel.read", "fuel.write"], Status = "Success" },
                "GET_FUEL_STATE" => new AarBridgeResponse { RequestId = request.RequestId, Action = request.Action, ProtocolVersion = 1, FuelState = State(_fuelKg), Status = "Success" },
                "APPLY_FUEL_DELTA" => new AarBridgeResponse
                {
                    RequestId = request.RequestId,
                    Action = request.Action,
                    ProtocolVersion = 1,
                    Status = "Failed",
                    Error = "Simulator only applied part of the requested update.",
                    RequestedKg = request.DeltaKg,
                    AppliedKg = 3,
                    FuelState = State(_fuelKg += 3)
                },
                _ => throw new InvalidOperationException("Unexpected bridge action: " + request.Action)
            };
            return Task.FromResult(response);
        }

        private static AarBridgeFuelState State(double fuelKg) => new()
        {
            SampledAtUtc = DateTimeOffset.UtcNow,
            CurrentFuelKg = fuelKg,
            CapacityKg = 500,
            FuelWeightPerGallonLb = 6.7,
            Tanks = [new AarBridgeFuelTank { TankId = "main", CurrentKg = fuelKg, CapacityKg = 500, Writable = true }]
        };
    }
}

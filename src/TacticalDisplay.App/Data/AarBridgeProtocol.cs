using System.Text.Json.Serialization;

namespace TacticalDisplay.App.Data;

public enum AarBridgeRuntimeState
{
    NotInstalled,
    InstalledNotRunning,
    Connecting,
    ConnectedReadOnly,
    ConnectedWritable,
    ProtocolMismatch,
    Error
}

public sealed record AarBridgeRequest(
    [property: JsonPropertyName("requestId")] string RequestId,
    [property: JsonPropertyName("action")] string Action,
    [property: JsonPropertyName("protocolVersion")] int ProtocolVersion,
    [property: JsonPropertyName("clientVersion")] string ClientVersion,
    [property: JsonPropertyName("deltaKg")] double? DeltaKg = null);

public sealed record AarBridgeResponse
{
    [JsonPropertyName("requestId")] public string? RequestId { get; init; }
    [JsonPropertyName("action")] public string? Action { get; init; }
    [JsonPropertyName("protocolVersion")] public int? ProtocolVersion { get; init; }
    [JsonPropertyName("bridgeVersion")] public string? BridgeVersion { get; init; }
    [JsonPropertyName("status")] public string? Status { get; init; }
    [JsonPropertyName("error")] public string? Error { get; init; }
    [JsonPropertyName("capabilities")] public string[] Capabilities { get; init; } = [];
    [JsonPropertyName("fuelState")] public AarBridgeFuelState? FuelState { get; init; }
    [JsonPropertyName("requestedKg")] public double? RequestedKg { get; init; }
    [JsonPropertyName("appliedKg")] public double? AppliedKg { get; init; }
    [JsonPropertyName("diagnostics")] public AarBridgeDiagnostics? Diagnostics { get; init; }
}

public sealed record AarBridgeDiagnostics
{
    [JsonPropertyName("fuelReadOnlyReason")] public string? FuelReadOnlyReason { get; init; }
    [JsonPropertyName("writeProbe")] public string[] WriteProbe { get; init; } = [];
}

public sealed record AarBridgeFuelState
{
    [JsonPropertyName("sampledAtUtc")] public DateTimeOffset SampledAtUtc { get; init; }
    [JsonPropertyName("currentFuelKg")] public double CurrentFuelKg { get; init; }
    [JsonPropertyName("capacityKg")] public double CapacityKg { get; init; }
    [JsonPropertyName("fuelWeightPerGallonLb")] public double FuelWeightPerGallonLb { get; init; }
    [JsonPropertyName("tanks")] public AarBridgeFuelTank[] Tanks { get; init; } = [];
}

public sealed record AarBridgeFuelTank
{
    [JsonPropertyName("tankId")] public string TankId { get; init; } = string.Empty;
    [JsonPropertyName("currentKg")] public double CurrentKg { get; init; }
    [JsonPropertyName("capacityKg")] public double CapacityKg { get; init; }
    [JsonPropertyName("writable")] public bool Writable { get; init; }
}

public sealed record AarBridgeTankDelta(string TankId, double DeltaKg);

public static class AarBridgeFuelDistributionPolicy
{
    public static IReadOnlyList<AarBridgeTankDelta> Distribute(IReadOnlyList<AarBridgeFuelTank> tanks, double deltaKg)
    {
        if (!double.IsFinite(deltaKg) || deltaKg == 0) throw new ArgumentOutOfRangeException(nameof(deltaKg));
        if (tanks.Count == 0 || tanks.Any(tank => !IsValid(tank)))
            throw new ArgumentException("Every tank must have finite, non-negative quantities within capacity.", nameof(tanks));

        var writable = tanks.Where(tank => tank.Writable).ToArray();
        if (writable.Length != tanks.Count)
            throw new InvalidOperationException("The aircraft fuel system contains a tank that is not verified writable.");

        var weights = writable.Select(tank => deltaKg > 0
            ? Math.Max(0, tank.CapacityKg - tank.CurrentKg)
            : Math.Max(0, tank.CurrentKg)).ToArray();
        var totalWeight = weights.Sum();
        if (totalWeight <= 0) return [];

        var magnitude = Math.Abs(deltaKg);
        var applicable = Math.Min(magnitude, totalWeight);
        var result = new List<AarBridgeTankDelta>(writable.Length);
        var remainder = applicable;
        for (var index = 0; index < writable.Length; index++)
        {
            var allocation = index == writable.Length - 1
                ? remainder
                : Math.Min(remainder, applicable * weights[index] / totalWeight);
            remainder -= allocation;
            if (allocation > 1e-9)
                result.Add(new AarBridgeTankDelta(writable[index].TankId, Math.Sign(deltaKg) * allocation));
        }
        return result;
    }

    private static bool IsValid(AarBridgeFuelTank tank) => !string.IsNullOrWhiteSpace(tank.TankId) &&
        double.IsFinite(tank.CurrentKg) && double.IsFinite(tank.CapacityKg) &&
        tank.CurrentKg >= 0 && tank.CapacityKg >= 0 && tank.CurrentKg <= tank.CapacityKg + 0.01;
}

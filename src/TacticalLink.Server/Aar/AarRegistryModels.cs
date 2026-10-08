using TacticalDisplay.Core.Models;
using System.Text.Json;

namespace TacticalLink.Server.Aar;

public sealed record AarRegistrySnapshot(
    int SchemaVersion,
    int Version,
    DateTimeOffset PublishedAt,
    IReadOnlyList<AarAircraftProfile> Profiles);

public sealed record AarAircraftProfile(
    string ProfileKey,
    string DisplayName,
    IReadOnlyList<string> IcaoDesignators,
    bool Enabled,
    bool CanTanker,
    bool CanReceive,
    IReadOnlyList<AarTankerSystem> TankerSystems,
    IReadOnlyList<AarReceiverSystem> ReceiverSystems,
    IReadOnlyList<AarRegistrySource> Sources,
    string? Uncertainty,
    string? Notes,
    JsonElement? Geometry = null);

public sealed record AarTankerSystem(string Method, double? MaxOffloadKgPerSecond, string ValueProvenance, IReadOnlyList<string> SourceIds);
public sealed record AarReceiverSystem(string Method, double? MaxReceiveKgPerSecond, string ValueProvenance, IReadOnlyList<string> SourceIds);
public sealed record AarRegistrySource(string Id, string Title, string Publisher, string Url, string Claim, DateTimeOffset RetrievedAt);

public interface IAarRegistryProvider
{
    AarRegistrySnapshot? Current { get; }
    bool IsAvailable { get; }
    string? Status { get; }
    event EventHandler? Changed;
}

public sealed record AarRegistryReadiness(bool Available, int? Version, string? Status)
{
    public static AarRegistryReadiness From(IAarRegistryProvider registry)
    {
        var snapshot = registry.Current;
        return new AarRegistryReadiness(snapshot is not null && registry.IsAvailable, snapshot?.Version, registry.Status);
    }
}

public sealed record TacticalLinkHealthResponse(string Status, AarRegistryReadiness AarRegistry)
{
    public static TacticalLinkHealthResponse From(IAarRegistryProvider registry) =>
        new("ok", AarRegistryReadiness.From(registry));
}

public sealed class AarAircraftCapabilityResolver(IAarRegistryProvider registry) : IAircraftCapabilityResolver
{
    public AircraftCapabilityProfile Resolve(string? aircraftType)
    {
        var normalized = new string((aircraftType ?? string.Empty).Where(char.IsAsciiLetterOrDigit).Select(char.ToUpperInvariant).ToArray());
        var profile = registry.Current?.Profiles.FirstOrDefault(candidate => candidate.Enabled && candidate.IcaoDesignators.Contains(normalized, StringComparer.Ordinal));
        return profile is null
            ? new AircraftCapabilityProfile(false, false)
            : new AircraftCapabilityProfile(profile.CanTanker, profile.CanReceive);
    }
}

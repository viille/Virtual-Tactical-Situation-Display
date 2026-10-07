using TacticalDisplay.Core.Models;
using TacticalLink.Server.Aar;
using Xunit;

namespace TacticalDisplay.Tests;

public sealed class AarRegistryTests
{
    [Fact]
    public void ResolverUsesThePublishedDesignatorAndDoesNotGrantUnknownCapability()
    {
        var snapshot = Snapshot(Profile("kc135", "K35R", canTanker: true, canReceive: false));
        var provider = new TestRegistryProvider(snapshot);
        var resolver = new AarAircraftCapabilityResolver(provider);

        Assert.True(resolver.Resolve(" k35r ").CanTanker);
        Assert.False(resolver.Resolve("KC135").CanTanker);
        Assert.False(resolver.Resolve("F16").CanReceive);
    }

    [Fact]
    public void RegistryRejectsDuplicateEnabledDesignators()
    {
        var first = Profile("kc46", "B762", canTanker: true, canReceive: false, uncertainty: "Shared B762 code.");
        var second = Profile("kc767", "B762", canTanker: true, canReceive: false, uncertainty: "Shared B762 code.");

        Assert.False(AarRegistryValidator.TryValidate(Snapshot(first, second), out var error));
        Assert.Contains("ambiguous", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DisabledProfilesMayShareADesignatorWithoutGrantingRuntimeCapability()
    {
        var enabled = Profile("kc46", "B762", canTanker: true, canReceive: false, uncertainty: "Shared B762 code.");
        var disabled = Profile("civil-767", "B762", canTanker: true, canReceive: false, enabled: false, uncertainty: "Shared B762 code.");
        var snapshot = Snapshot(enabled, disabled);

        Assert.True(AarRegistryValidator.TryValidate(snapshot, out _));
        Assert.True(new AarAircraftCapabilityResolver(new TestRegistryProvider(snapshot)).Resolve("B762").CanTanker);
    }

    [Fact]
    public void RegistryRejectsFallbackNumbersPresentedAsAircraftSpecificValues()
    {
        var profile = Profile("kc135", "K35R", true, false) with
        {
            TankerSystems = [new AarTankerSystem("Boom", 10, "unknown", [])],
        };

        Assert.False(AarRegistryValidator.TryValidate(Snapshot(profile), out _));
    }

    [Fact]
    public void RegistryRejectsConfirmedRatesWithoutAProfileSource()
    {
        var profile = Profile("kc135", "K35R", true, false) with
        {
            TankerSystems = [new AarTankerSystem("Boom", 25, "confirmed_aircraft_specific_value", [])],
        };

        Assert.False(AarRegistryValidator.TryValidate(Snapshot(profile), out _));
    }

    private static AarRegistrySnapshot Snapshot(params AarAircraftProfile[] profiles) =>
        new(1, 1, DateTimeOffset.Parse("2026-10-07T00:00:00Z"), profiles);

    private static AarAircraftProfile Profile(string key, string designator, bool canTanker, bool canReceive, bool enabled = true, string? uncertainty = null) =>
        new(key, key, [designator], enabled, canTanker, canReceive,
            canTanker ? [new AarTankerSystem("Boom", null, "unknown", [])] : [],
            canReceive ? [new AarReceiverSystem("BoomReceptacle", null, "unknown", [])] : [],
            [], uncertainty, null);

    private sealed class TestRegistryProvider(AarRegistrySnapshot? current) : IAarRegistryProvider
    {
        public AarRegistrySnapshot? Current => current;
        public bool IsAvailable => current is not null;
        public string? Status => null;
        public event EventHandler? Changed { add { } remove { } }
    }
}

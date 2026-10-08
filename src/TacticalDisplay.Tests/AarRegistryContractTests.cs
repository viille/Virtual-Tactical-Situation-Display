using System.Text.Json;
using TacticalLink.Server.Aar;

namespace TacticalDisplay.Tests;

public sealed class AarRegistryContractTests
{
    [Fact]
    public void PublishedCloudSnapshotFixtureDeserializesAndPassesServerValidation()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "TestData", "aar-registry-published.json");
        var snapshot = JsonSerializer.Deserialize<AarRegistrySnapshot>(File.ReadAllText(path), new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.True(AarRegistryValidator.TryValidate(snapshot, out var reason), reason);
        Assert.Contains(snapshot!.Profiles, profile => profile.IcaoDesignators.Contains("K35R") && profile.CanTanker);
        Assert.Contains(snapshot.Profiles, profile => profile.IcaoDesignators.Contains("F18") && profile.CanReceive && profile.ReceiverSystems.Single().Method == "Probe");
        Assert.Contains(snapshot.Profiles, profile => profile.IcaoDesignators.Contains("F35") && profile.CanReceive && profile.ReceiverSystems.Single().Method == "BoomReceptacle");
        Assert.All(snapshot.Profiles.Where(profile => profile.CanReceive).SelectMany(profile => profile.ReceiverSystems), system => Assert.Null(system.MaxReceiveKgPerSecond));
    }
}

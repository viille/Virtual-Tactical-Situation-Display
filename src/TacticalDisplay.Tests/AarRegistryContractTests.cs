using System.Text.Json;
using TacticalLink.Server.Aar;
using Xunit;

namespace TacticalDisplay.Tests;

public sealed class AarRegistryContractTests
{
    [Theory]
    [InlineData("https://example.org/source", true)]
    [InlineData("http://example.org/source", true)]
    [InlineData("ftp://example.org/source", false)]
    [InlineData("file:///source", false)]
    public void SourceUrlContractAllowsOnlyHttpAndHttps(string url, bool expected)
    {
        var profile = ContractProfile() with
        {
            Sources = [new AarRegistrySource("source-1", "Reference", "Publisher", url, "Supports the measured value.", DateTimeOffset.UtcNow)],
        };

        Assert.Equal(expected, AarRegistryValidator.TryValidate(new AarRegistrySnapshot(1, 1, DateTimeOffset.UtcNow, [profile]), out _));
    }

    [Theory]
    [InlineData("title")]
    [InlineData("publisher")]
    [InlineData("claim")]
    public void SourceContractRejectsWhitespaceOnlyEvidenceText(string field)
    {
        var source = new AarRegistrySource("source-1", "Reference", "Publisher", "https://example.org/source", "Supports the measured value.", DateTimeOffset.UtcNow);
        source = field switch
        {
            "title" => source with { Title = "   " },
            "publisher" => source with { Publisher = "   " },
            _ => source with { Claim = "   " },
        };
        var profile = ContractProfile() with { Sources = [source] };

        Assert.False(AarRegistryValidator.TryValidate(new AarRegistrySnapshot(1, 1, DateTimeOffset.UtcNow, [profile]), out _));
    }

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

    private static AarAircraftProfile ContractProfile() => new("test-tanker", "Test tanker", ["TST1"], true, true, false,
        [new AarTankerSystem("Boom", null, "unknown", [])], [], [], null, "");
}

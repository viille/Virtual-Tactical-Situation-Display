using System.Text.Json;
using TacticalDisplay.Core.Config;
using TacticalDisplay.Core.Models;
using Xunit;

namespace TacticalDisplay.Tests;

public sealed class JsonConfigStoreTests
{
    [Fact]
    public void LoadDisplaySettings_WhenJsonIsInvalid_ReplacesWithDefaults()
    {
        var directory = CreateTempDirectory();
        var path = Path.Combine(directory, "display.json");
        File.WriteAllText(path, "{ invalid json");

        var store = new JsonConfigStore(directory);

        var settings = store.LoadDisplaySettings();

        Assert.Equal("Demo", settings.DataSourceMode);
        Assert.Equal(40, settings.SelectedRangeNm);

        var reloaded = JsonSerializer.Deserialize<TacticalDisplaySettings>(File.ReadAllText(path));
        Assert.NotNull(reloaded);
        Assert.Equal("Demo", reloaded!.DataSourceMode);
        Assert.Equal(40, reloaded.SelectedRangeNm);
        Assert.Single(Directory.GetFiles(directory, "display.json.corrupt-*"));
    }

    [Fact]
    public void LoadDisplaySettings_WhenValuesAreInvalid_ReplacesWithDefaults()
    {
        var directory = CreateTempDirectory();
        var path = Path.Combine(directory, "display.json");
        File.WriteAllText(path, """
        {
          "selectedRangeNm": 999,
          "rangeScaleOptionsNm": [10, 20, 40],
          "renderRateFps": 0
        }
        """);

        var store = new JsonConfigStore(directory);

        var settings = store.LoadDisplaySettings();

        Assert.Equal(40, settings.SelectedRangeNm);
        Assert.Equal(24, settings.RenderRateFps);
        Assert.Single(Directory.GetFiles(directory, "display.json.corrupt-*"));
    }

    [Fact]
    public void LoadDisplaySettings_WhenUsingDefaults_IncludesFinlandAndEstoniaLara()
    {
        var directory = CreateTempDirectory();
        var store = new JsonConfigStore(directory);

        var settings = store.LoadDisplaySettings();

        Assert.Equal(["efin", "eett"], settings.AirspaceFirCodes);
        Assert.Contains("https://lara-backend.lusep.fi/data/reservations/efin.json", settings.AirspaceActivationUrls);
        Assert.Contains("https://lara-backend.lusep.fi/data/reservations/eett.json", settings.AirspaceActivationUrls);
    }

    [Fact]
    public void LoadDisplaySettings_WhenSingleAirspaceFirIsConfigured_PreservesRegionSelection()
    {
        var directory = CreateTempDirectory();
        var path = Path.Combine(directory, "display.json");
        File.WriteAllText(path, """
        {
          "RangeScaleOptionsNm": [10, 20, 40],
          "SelectedRangeNm": 40,
          "XPlane12ApiBaseUrl": "http://localhost:8086/",
          "VatsimDataFeedUrl": "https://data.vatsim.net/v3/vatsim-data.json",
          "AirspaceFirCode": "efin",
          "AirspaceFirCodes": ["efin"],
          "AirspaceDataBaseUrl": "https://raw.githubusercontent.com/ottotuhkunen/virtual-lara-airspace-data/main/data",
          "AirspaceActivationUrl": "https://lara-backend.lusep.fi/data/reservations/efin.json",
          "RenderRateFps": 24,
          "PollRateHz": 8,
          "StaleSeconds": 4,
          "RemoveAfterSeconds": 12,
          "MinTrackedAltitudeFt": 200,
          "WindowWidth": 1280,
          "WindowHeight": 840,
          "TargetSymbolScale": 1,
          "VatsimCallsignRefreshSeconds": 15,
          "TrailLengthSamples": 90,
          "KneepadPages": [
            { "ContentMode": "Empty" }
          ],
          "Hotkeys": []
        }
        """);

        var store = new JsonConfigStore(directory);

        var settings = store.LoadDisplaySettings();

        Assert.Equal(["efin"], settings.AirspaceFirCodes);
    }

    [Fact]
    public void LoadManualTargetMetadata_WhenJsonIsInvalid_ReplacesWithEmptyMap()
    {
        var directory = CreateTempDirectory();
        var path = Path.Combine(directory, "manual-targets.json");
        File.WriteAllText(path, "[]");

        var store = new JsonConfigStore(directory);

        var metadata = store.LoadManualTargetMetadata();

        Assert.Empty(metadata);
        Assert.Equal("{}", File.ReadAllText(path));
        Assert.Single(Directory.GetFiles(directory, "manual-targets.json.corrupt-*"));
    }

    private static string CreateTempDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "VTSD-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }
}

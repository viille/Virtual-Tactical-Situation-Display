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
    }

    private static string CreateTempDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "VTSD-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }
}

using TacticalDisplay.App.Services;
using Xunit;

namespace TacticalDisplay.Tests;

public sealed class DataSourceDebugLogTests
{
    [Fact]
    public void ThrottledDebug_DoesNotBuildMessageWhenLoggingIsDisabled()
    {
        var wasEnabled = DataSourceDebugLog.IsEnabled;
        var messageBuilt = false;
        try
        {
            DataSourceDebugLog.SetEnabled(false);

            DataSourceDebugLog.ThrottledDebug(
                "Test",
                $"disabled-{Guid.NewGuid():N}",
                TimeSpan.FromSeconds(5),
                () =>
                {
                    messageBuilt = true;
                    return "unused";
                });

            Assert.False(messageBuilt);
        }
        finally
        {
            DataSourceDebugLog.SetEnabled(wasEnabled);
        }
    }
}

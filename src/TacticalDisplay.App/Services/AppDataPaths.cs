using System.IO;

namespace TacticalDisplay.App.Services;

public static class AppDataPaths
{
    private const string ApplicationDirectoryName = "VirtualTacticalSituationDisplay";

    public static string ApplicationDataDirectory => Path.Combine(GetRoamingAppDataDirectory(), ApplicationDirectoryName);
    public static string WebViewUserDataDirectory => Path.Combine(ApplicationDataDirectory, "WebView2");
    public static string DataSourceDebugLogFilePath => Path.Combine(ApplicationDataDirectory, "logs", "data-source-debug.log");

    public static void MigrateLegacyConfigIfNeeded()
    {
        var targetDirectory = ApplicationDataDirectory;
        if (Directory.Exists(targetDirectory))
        {
            return;
        }

        var legacyDirectory = ResolveLegacyConfigDirectory();
        if (legacyDirectory is null || !Directory.Exists(legacyDirectory))
        {
            return;
        }

        try
        {
            CopyDirectory(legacyDirectory, targetDirectory);
        }
        catch
        {
            // Legacy config migration is best-effort; startup will create clean files if migration fails.
        }
    }

    private static string GetRoamingAppDataDirectory()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return string.IsNullOrWhiteSpace(appData) ? Path.GetTempPath() : appData;
    }

    private static string? ResolveLegacyConfigDirectory()
    {
        var baseDir = AppContext.BaseDirectory;
        var repoConfig = Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..", "config"));
        if (Directory.Exists(repoConfig))
        {
            return repoConfig;
        }

        var executableConfig = Path.Combine(baseDir, "config");
        return Directory.Exists(executableConfig) ? executableConfig : null;
    }

    private static void CopyDirectory(string sourceDirectory, string targetDirectory)
    {
        Directory.CreateDirectory(targetDirectory);

        foreach (var sourceFile in Directory.EnumerateFiles(sourceDirectory))
        {
            var targetFile = Path.Combine(targetDirectory, Path.GetFileName(sourceFile));
            File.Copy(sourceFile, targetFile, overwrite: false);
        }

        foreach (var sourceSubdirectory in Directory.EnumerateDirectories(sourceDirectory))
        {
            var targetSubdirectory = Path.Combine(targetDirectory, Path.GetFileName(sourceSubdirectory));
            CopyDirectory(sourceSubdirectory, targetSubdirectory);
        }
    }
}

using System.Diagnostics;

const int maxAttempts = 60;
const int delayMs = 500;

try
{
    if (args.Length < 3)
    {
        return 2;
    }

    if (!int.TryParse(args[0], out var processId))
    {
        return 3;
    }

    var sourceExePath = Path.GetFullPath(args[1]);
    var targetExePath = Path.GetFullPath(args[2]);
    var targetDirectory = Path.GetDirectoryName(targetExePath);
    if (string.IsNullOrWhiteSpace(targetDirectory) ||
        !File.Exists(sourceExePath) ||
        !File.Exists(targetExePath))
    {
        return 4;
    }

    WaitForProcessExit(processId);

    var backupExePath = Path.Combine(
        targetDirectory,
        $"{Path.GetFileNameWithoutExtension(targetExePath)}.previous{Path.GetExtension(targetExePath)}");
    var stagedExePath = Path.Combine(
        targetDirectory,
        $"{Path.GetFileNameWithoutExtension(targetExePath)}.update{Path.GetExtension(targetExePath)}");

    for (var attempt = 0; attempt < maxAttempts; attempt++)
    {
        try
        {
            if (File.Exists(stagedExePath))
            {
                File.Delete(stagedExePath);
            }

            File.Copy(sourceExePath, stagedExePath, overwrite: true);

            if (File.Exists(backupExePath))
            {
                File.Delete(backupExePath);
            }

            File.Move(targetExePath, backupExePath);
            File.Move(stagedExePath, targetExePath);

            StartUpdatedApp(targetExePath);
            TryDelete(sourceExePath);
            TryDelete(stagedExePath);
            TryDelete(backupExePath);
            return 0;
        }
        catch
        {
            TryRestoreBackup(targetExePath, backupExePath);
            TryDelete(stagedExePath);
            Thread.Sleep(delayMs);
        }
    }

    return 5;
}
catch
{
    return 1;
}

static void WaitForProcessExit(int processId)
{
    try
    {
        using var process = Process.GetProcessById(processId);
        if (!process.HasExited)
        {
            process.WaitForExit(30_000);
        }
    }
    catch
    {
        // The app process may already be gone.
    }
}

static void StartUpdatedApp(string targetExePath)
{
    Process.Start(new ProcessStartInfo
    {
        FileName = targetExePath,
        WorkingDirectory = Path.GetDirectoryName(targetExePath),
        UseShellExecute = true
    });
}

static void TryRestoreBackup(string targetExePath, string backupExePath)
{
    try
    {
        if (!File.Exists(targetExePath) && File.Exists(backupExePath))
        {
            File.Move(backupExePath, targetExePath);
        }
    }
    catch
    {
    }
}

static void TryDelete(string path)
{
    try
    {
        File.Delete(path);
    }
    catch
    {
    }
}

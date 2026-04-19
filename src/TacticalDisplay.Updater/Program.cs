using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;

const int maxAttempts = 60;
const int delayMs = 500;
Application.EnableVisualStyles();
Application.SetCompatibleTextRenderingDefault(false);
using var statusWindow = new UpdateStatusWindow();
statusWindow.Show();
Application.DoEvents();

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

    statusWindow.SetStatus("Waiting for app to close...");
    for (var attempt = 0; attempt < maxAttempts; attempt++)
    {
        try
        {
            statusWindow.SetStatus($"Installing update... ({attempt + 1}/{maxAttempts})");
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

            statusWindow.SetStatus("Starting updated app...");
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

    statusWindow.SetStatus("Update failed.");
    Thread.Sleep(2500);
    return 5;
}
catch
{
    statusWindow.SetStatus("Update failed.");
    Thread.Sleep(2500);
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

internal sealed class UpdateStatusWindow : Form
{
    private readonly Label _statusLabel = new()
    {
        AutoSize = false,
        Dock = DockStyle.Top,
        Height = 44,
        TextAlign = ContentAlignment.MiddleLeft,
        ForeColor = Color.FromArgb(217, 242, 236),
        Font = new Font("Consolas", 10, FontStyle.Regular)
    };

    private readonly ProgressBar _progress = new()
    {
        Dock = DockStyle.Top,
        Height = 18,
        Style = ProgressBarStyle.Marquee,
        MarqueeAnimationSpeed = 25
    };

    public UpdateStatusWindow()
    {
        Text = "Updating Tactical Display";
        Width = 420;
        Height = 145;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Color.FromArgb(7, 16, 21);
        ShowInTaskbar = true;

        var panel = new Panel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(14),
            BackColor = Color.FromArgb(16, 32, 40)
        };
        var title = new Label
        {
            AutoSize = false,
            Dock = DockStyle.Top,
            Height = 28,
            Text = "Installing update",
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = Color.FromArgb(154, 250, 215),
            Font = new Font("Consolas", 12, FontStyle.Bold)
        };

        panel.Controls.Add(_progress);
        panel.Controls.Add(_statusLabel);
        panel.Controls.Add(title);
        Controls.Add(panel);
        SetStatus("Preparing update...");
    }

    public void SetStatus(string text)
    {
        _statusLabel.Text = text;
        Application.DoEvents();
    }
}

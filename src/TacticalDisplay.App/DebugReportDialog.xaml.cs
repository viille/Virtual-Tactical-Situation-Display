using System.Text;
using System.Windows;
using TacticalDisplay.App.Services;
using TacticalDisplay.Core.Models;

namespace TacticalDisplay.App;

public partial class DebugReportDialog : Window
{
    private readonly DebugReportUploadService _uploadService;
    private PreparedDebugReport? _preparedReport;

    public DebugReportDialog(
        string appVersion,
        TacticalDisplaySettings settings,
        TelemetryService telemetryService)
    {
        InitializeComponent();
        _uploadService = new DebugReportUploadService(appVersion, settings, telemetryService);
    }

    protected override void OnClosed(EventArgs e)
    {
        _preparedReport?.Dispose();
        base.OnClosed(e);
    }

    private async void OnReviewClicked(object sender, RoutedEventArgs e)
    {
        SetBusy(true, "Preparing debug report...");
        _preparedReport?.Dispose();
        _preparedReport = null;
        SummaryPanel.Visibility = Visibility.Collapsed;
        SendButton.IsEnabled = false;

        try
        {
            var options = new DebugReportOptions(
                IncludeLogsCheck.IsChecked == true,
                IncludeSettingsCheck.IsChecked == true,
                IncludeDiagnosticsCheck.IsChecked == true,
                DescriptionBox.Text.Trim());
            _preparedReport = await _uploadService.PrepareAsync(options, CancellationToken.None);
            SummaryText.Text = BuildSummary(_preparedReport);
            SummaryPanel.Visibility = Visibility.Visible;
            SendButton.IsEnabled = true;
            StatusText.Text = "Review the package summary, then click Send to upload.";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Could not prepare debug report: {ex.Message}";
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void OnSendClicked(object sender, RoutedEventArgs e)
    {
        if (_preparedReport is null)
        {
            return;
        }

        var result = MessageBox.Show(
            this,
            "Upload this raw debug report now?\n\nThe package is not anonymized, masked, or scrubbed.",
            "Confirm Debug Report Upload",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (result != MessageBoxResult.Yes)
        {
            return;
        }

        SetBusy(true, "Uploading debug report...");
        try
        {
            await _uploadService.UploadAsync(_preparedReport, CancellationToken.None);
            StatusText.Text = "Debug report uploaded.";
            DialogResult = true;
            Close();
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Debug report upload failed: {ex.Message}";
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void SetBusy(bool busy, string? status = null)
    {
        ReviewButton.IsEnabled = !busy;
        SendButton.IsEnabled = !busy && _preparedReport is not null;
        IncludeLogsCheck.IsEnabled = !busy;
        IncludeSettingsCheck.IsEnabled = !busy;
        IncludeDiagnosticsCheck.IsEnabled = !busy;
        DescriptionBox.IsEnabled = !busy;
        if (status is not null)
        {
            StatusText.Text = status;
        }
    }

    private static string BuildSummary(PreparedDebugReport report)
    {
        var builder = new StringBuilder();
        builder.AppendLine("Package: debug-report.zip");
        builder.AppendLine($"Estimated size: {FormatBytes(report.SizeBytes)}");
        builder.AppendLine($"Created UTC: {report.Metadata.CreatedAtUtc:O}");
        builder.AppendLine();
        builder.AppendLine("Included files:");
        foreach (var entry in report.Entries)
        {
            builder.AppendLine($"- {entry.Path} ({FormatBytes(entry.SizeBytes)})");
        }

        return builder.ToString();
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB"];
        double value = bytes;
        var unitIndex = 0;
        while (value >= 1024 && unitIndex < units.Length - 1)
        {
            value /= 1024;
            unitIndex++;
        }

        return $"{value:0.##} {units[unitIndex]}";
    }
}

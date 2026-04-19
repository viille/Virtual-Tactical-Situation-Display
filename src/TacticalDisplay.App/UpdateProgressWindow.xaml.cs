using System.Windows;
using TacticalDisplay.App.Services;

namespace TacticalDisplay.App;

public partial class UpdateProgressWindow : Window
{
    public UpdateProgressWindow()
    {
        InitializeComponent();
    }

    public void Update(UpdateProgress progress)
    {
        StatusText.Text = progress.Percent.HasValue
            ? $"{progress.Message} {progress.Percent.Value:0}%"
            : progress.Message;
        Progress.IsIndeterminate = !progress.Percent.HasValue;
        if (progress.Percent.HasValue)
        {
            Progress.Value = progress.Percent.Value;
        }
    }
}

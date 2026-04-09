using System.Windows;

namespace TacticalDisplay.App;

public partial class SimConnectDebugDialog : Window
{
    public SimConnectDebugDialog(string message)
    {
        InitializeComponent();
        MessageText.Text = message;
    }

    public SimConnectDebugChoice Choice { get; private set; } = SimConnectDebugChoice.Cancel;

    private void OnSelectMsfsClicked(object sender, RoutedEventArgs e)
    {
        Choice = SimConnectDebugChoice.SelectMsfs;
        DialogResult = true;
        Close();
    }

    private void OnSelectDllClicked(object sender, RoutedEventArgs e)
    {
        Choice = SimConnectDebugChoice.SelectDll;
        DialogResult = true;
        Close();
    }

    private void OnCancelClicked(object sender, RoutedEventArgs e)
    {
        Choice = SimConnectDebugChoice.Cancel;
        DialogResult = false;
        Close();
    }
}

public enum SimConnectDebugChoice
{
    Cancel,
    SelectMsfs,
    SelectDll
}

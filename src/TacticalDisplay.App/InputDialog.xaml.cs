using System.Windows;

namespace TacticalDisplay.App;

public partial class InputDialog : Window
{
    public InputDialog(string title, string prompt, string initialValue = "")
    {
        InitializeComponent();
        Title = title;
        PromptText.Text = prompt;
        InputBox.Text = initialValue;
        InputBox.SelectAll();
        InputBox.Focus();
    }

    public string Value => InputBox.Text.Trim();

    private void OnOkClicked(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }
}

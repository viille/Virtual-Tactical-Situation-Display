using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace TacticalDisplay.App;

public partial class InputDialog : Window
{
    private readonly DateTimeOffset _ignoreTextBoxRightClickUntil;

    public InputDialog(string title, string prompt, string initialValue = "")
    {
        InitializeComponent();
        _ignoreTextBoxRightClickUntil = DateTimeOffset.UtcNow.AddMilliseconds(500);
        Title = title;
        PromptText.Text = prompt;
        InputBox.Text = initialValue;
        InputBox.SelectAll();
        InputBox.Focus();
    }

    public string Value => InputBox.Text.Trim();

    private bool ShouldIgnoreInitialRightClick()
    {
        return DateTimeOffset.UtcNow <= _ignoreTextBoxRightClickUntil;
    }

    private void OnInputBoxInitialRightClick(object sender, MouseButtonEventArgs e)
    {
        if (ShouldIgnoreInitialRightClick())
        {
            e.Handled = true;
        }
    }

    private void OnInputBoxContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (ShouldIgnoreInitialRightClick())
        {
            e.Handled = true;
        }
    }

    private void OnOkClicked(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }
}

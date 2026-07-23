using System.Windows;
using System.Windows.Input;
using TacticalDisplay.App.Services;
using TacticalDisplay.App.ViewModels;

namespace TacticalDisplay.App;

public partial class HotkeyConfigDialog : Window
{
    private readonly GlobalHotkeyService _hotkeyService;
    private HotkeyBindingViewModel? _capturingHotkeyRow;
    private string? _capturingHotkeyDevice;

    public HotkeyConfigDialog(MainViewModel viewModel, GlobalHotkeyService hotkeyService)
    {
        InitializeComponent();
        DataContext = viewModel;
        _hotkeyService = hotkeyService;
        _hotkeyService.GamepadButtonPressed += OnGamepadButtonPressed;
        _hotkeyService.JoystickButtonPressed += OnJoystickButtonPressed;
        PreviewKeyDown += OnWindowPreviewKeyDown;
        Closed += OnClosed;
    }

    private void OnSetKeyboardHotkeyClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: HotkeyBindingViewModel row })
        {
            StartHotkeyCapture(row, "keyboard");
        }
    }

    private void OnSetGamepadHotkeyClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: HotkeyBindingViewModel row })
        {
            StartHotkeyCapture(row, "gamepad");
        }
    }

    private void StartHotkeyCapture(HotkeyBindingViewModel row, string device)
    {
        ClearHotkeyCapture();
        _capturingHotkeyRow = row;
        _capturingHotkeyDevice = device;
        _hotkeyService.SuppressActions = true;
        if (device == "keyboard")
        {
            row.IsKeyboardCaptureActive = true;
            Focus();
        }
        else
        {
            row.IsGamepadCaptureActive = true;
        }
    }

    private void ClearHotkeyCapture()
    {
        if (_capturingHotkeyRow is not null)
        {
            _capturingHotkeyRow.IsKeyboardCaptureActive = false;
            _capturingHotkeyRow.IsGamepadCaptureActive = false;
        }

        _capturingHotkeyRow = null;
        _capturingHotkeyDevice = null;
        _hotkeyService.SuppressActions = false;
    }

    private void FinishHotkeyCapture()
    {
        if (_capturingHotkeyRow is not null)
        {
            _capturingHotkeyRow.IsKeyboardCaptureActive = false;
            _capturingHotkeyRow.IsGamepadCaptureActive = false;
        }

        _capturingHotkeyRow = null;
        _capturingHotkeyDevice = null;
        _hotkeyService.SuppressActions = false;
    }

    private void OnWindowPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_capturingHotkeyRow is null || _capturingHotkeyDevice != "keyboard")
        {
            return;
        }

        var key = e.Key == Key.System ? e.SystemKey :
            e.Key == Key.ImeProcessed ? e.ImeProcessedKey :
            e.Key;
        if (key == Key.Escape)
        {
            ClearHotkeyCapture();
            e.Handled = true;
            return;
        }

        if (key is Key.Delete or Key.Back)
        {
            _capturingHotkeyRow.SetKeyboard(string.Empty);
            FinishHotkeyCapture();
            e.Handled = true;
            return;
        }

        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin)
        {
            e.Handled = true;
            return;
        }

        _capturingHotkeyRow.SetKeyboard(FormatKeyboardBinding(key, Keyboard.Modifiers));
        FinishHotkeyCapture();
        e.Handled = true;
    }

    private void OnGamepadButtonPressed(object? sender, GamepadButtonPressedEventArgs e)
    {
        if (_capturingHotkeyRow is null || _capturingHotkeyDevice != "gamepad")
        {
            return;
        }

        _capturingHotkeyRow.SetGamepad(e.Button);
        FinishHotkeyCapture();
    }

    private void OnJoystickButtonPressed(object? sender, JoystickButtonPressedEventArgs e)
    {
        if (_capturingHotkeyRow is null || _capturingHotkeyDevice != "gamepad")
        {
            return;
        }

        _capturingHotkeyRow.SetGamepad(e.BindingText);
        FinishHotkeyCapture();
    }

    private void OnCloseClick(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        ClearHotkeyCapture();
        _hotkeyService.GamepadButtonPressed -= OnGamepadButtonPressed;
        _hotkeyService.JoystickButtonPressed -= OnJoystickButtonPressed;
        PreviewKeyDown -= OnWindowPreviewKeyDown;
        Closed -= OnClosed;
    }

    private static string FormatKeyboardBinding(Key key, ModifierKeys modifiers)
    {
        var parts = new List<string>();
        if ((modifiers & ModifierKeys.Control) != 0)
        {
            parts.Add("Ctrl");
        }

        if ((modifiers & ModifierKeys.Alt) != 0)
        {
            parts.Add("Alt");
        }

        if ((modifiers & ModifierKeys.Shift) != 0)
        {
            parts.Add("Shift");
        }

        if ((modifiers & ModifierKeys.Windows) != 0)
        {
            parts.Add("Win");
        }

        parts.Add(key switch
        {
            Key.Prior => "PageUp",
            Key.Next => "PageDown",
            Key.Return => "Enter",
            Key.Escape => "Esc",
            _ => key.ToString()
        });
        return string.Join("+", parts);
    }
}

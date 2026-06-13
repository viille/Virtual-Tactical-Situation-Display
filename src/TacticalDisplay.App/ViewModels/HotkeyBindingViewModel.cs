using TacticalDisplay.Core.Models;

namespace TacticalDisplay.App.ViewModels;

public sealed class HotkeyBindingViewModel : ViewModelBase
{
    private readonly HotkeyBinding _binding;
    private readonly Action _changed;
    private bool _isKeyboardCaptureActive;
    private bool _isGamepadCaptureActive;

    public HotkeyBindingViewModel(string displayName, HotkeyBinding binding, Action changed)
    {
        DisplayName = displayName;
        _binding = binding;
        _changed = changed;
    }

    public string DisplayName { get; }

    public bool IsEnabled
    {
        get => _binding.IsEnabled;
        set
        {
            if (_binding.IsEnabled == value)
            {
                return;
            }

            _binding.IsEnabled = value;
            Raise();
            _changed();
        }
    }

    public string Keyboard
    {
        get => _binding.Keyboard;
        set
        {
            var normalized = value.Trim();
            if (_binding.Keyboard == normalized)
            {
                return;
            }

            _binding.Keyboard = normalized;
            Raise();
            _changed();
        }
    }

    public string Gamepad
    {
        get => _binding.Gamepad;
        set
        {
            var normalized = value.Trim();
            if (_binding.Gamepad == normalized)
            {
                return;
            }

            _binding.Gamepad = normalized;
            Raise();
            _changed();
        }
    }

    public string KeyboardDisplay => IsKeyboardCaptureActive
        ? "Press..."
        : string.IsNullOrWhiteSpace(Keyboard) ? "Unassigned" : Keyboard;

    public string GamepadDisplay => IsGamepadCaptureActive
        ? "Press..."
        : string.IsNullOrWhiteSpace(Gamepad) ? "Unassigned" : Gamepad;

    public bool IsKeyboardCaptureActive
    {
        get => _isKeyboardCaptureActive;
        set
        {
            if (_isKeyboardCaptureActive == value)
            {
                return;
            }

            _isKeyboardCaptureActive = value;
            Raise();
            Raise(nameof(KeyboardDisplay));
        }
    }

    public bool IsGamepadCaptureActive
    {
        get => _isGamepadCaptureActive;
        set
        {
            if (_isGamepadCaptureActive == value)
            {
                return;
            }

            _isGamepadCaptureActive = value;
            Raise();
            Raise(nameof(GamepadDisplay));
        }
    }

    public void SetKeyboard(string value)
    {
        Keyboard = value;
        Raise(nameof(KeyboardDisplay));
    }

    public void SetGamepad(string value)
    {
        Gamepad = value;
        Raise(nameof(GamepadDisplay));
    }
}

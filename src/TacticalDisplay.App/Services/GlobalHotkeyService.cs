using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Threading;
using TacticalDisplay.Core.Models;

namespace TacticalDisplay.App.Services;

public sealed class GlobalHotkeyService : IDisposable
{
    private const int WhKeyboardLl = 13;
    private const int WmKeyDown = 0x0100;
    private const int WmKeyUp = 0x0101;
    private const int WmSysKeyDown = 0x0104;
    private const int WmSysKeyUp = 0x0105;
    private const int XInputMaxControllers = 4;

    private readonly Dispatcher _dispatcher;
    private readonly Action<string> _executeAction;
    private readonly LowLevelKeyboardProc _keyboardProc;
    private readonly DispatcherTimer _gamepadTimer;
    private readonly HashSet<string> _pressedKeyboardBindings = new(StringComparer.OrdinalIgnoreCase);
    private readonly XInputState[] _previousGamepadStates = new XInputState[XInputMaxControllers];
    private readonly bool[] _previousGamepadConnected = new bool[XInputMaxControllers];
    private Dictionary<string, string> _keyboardBindings = new(StringComparer.OrdinalIgnoreCase);
    private List<GamepadBinding> _gamepadBindings = [];
    private IntPtr _keyboardHook;
    private bool _disposed;

    public GlobalHotkeyService(Dispatcher dispatcher, Action<string> executeAction)
    {
        _dispatcher = dispatcher;
        _executeAction = executeAction;
        _keyboardProc = KeyboardHookCallback;
        _gamepadTimer = new DispatcherTimer(DispatcherPriority.Background, dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(50)
        };
        _gamepadTimer.Tick += (_, _) => PollGamepads();
    }

    public event EventHandler<GamepadButtonPressedEventArgs>? GamepadButtonPressed;

    public bool SuppressActions { get; set; }

    public void Start(IEnumerable<HotkeyBinding> bindings)
    {
        UpdateBindings(bindings);
        if (_keyboardHook == IntPtr.Zero)
        {
            _keyboardHook = SetKeyboardHook(_keyboardProc);
            if (_keyboardHook == IntPtr.Zero)
            {
                DataSourceDebugLog.Warn("Input", $"Global keyboard hook failed | win32={Marshal.GetLastWin32Error()}");
            }
        }

        if (!_gamepadTimer.IsEnabled)
        {
            _gamepadTimer.Start();
        }
    }

    public void UpdateBindings(IEnumerable<HotkeyBinding> bindings)
    {
        var keyboardBindings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var gamepadBindings = new List<GamepadBinding>();

        foreach (var binding in bindings.Where(static binding => binding.IsEnabled))
        {
            if (TryNormalizeKeyboardBinding(binding.Keyboard, out var keyboard))
            {
                keyboardBindings[keyboard] = binding.Action;
            }

            if (TryParseGamepadBinding(binding.Gamepad, binding.Action, out var gamepad))
            {
                gamepadBindings.Add(gamepad);
            }
        }

        _keyboardBindings = keyboardBindings;
        _gamepadBindings = gamepadBindings;
        _pressedKeyboardBindings.Clear();
    }

    private IntPtr KeyboardHookCallback(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0 && (wParam == WmKeyDown || wParam == WmSysKeyDown || wParam == WmKeyUp || wParam == WmSysKeyUp))
        {
            var hook = Marshal.PtrToStructure<KbdLlHookStruct>(lParam);
            if (TryBuildKeyboardBinding(hook.VkCode, out var binding))
            {
                if (wParam == WmKeyUp || wParam == WmSysKeyUp)
                {
                    ReleasePressedKeyboardBinding(hook.VkCode);
                }
                else if (_keyboardBindings.TryGetValue(binding, out var action) &&
                         _pressedKeyboardBindings.Add(binding))
                {
                    Dispatch(action);
                }
            }
        }

        return CallNextHookEx(_keyboardHook, code, wParam, lParam);
    }

    private void ReleasePressedKeyboardBinding(uint virtualKey)
    {
        var key = KeyInterop.KeyFromVirtualKey((int)virtualKey);
        if (key == Key.None)
        {
            return;
        }

        var keyName = NormalizeKeyName(key);
        foreach (var pressed in _pressedKeyboardBindings
                     .Where(pressed => pressed.Equals(keyName, StringComparison.OrdinalIgnoreCase) ||
                         pressed.EndsWith($"+{keyName}", StringComparison.OrdinalIgnoreCase))
                     .ToList())
        {
            _pressedKeyboardBindings.Remove(pressed);
        }
    }

    private bool TryBuildKeyboardBinding(uint virtualKey, out string binding)
    {
        binding = string.Empty;
        var key = KeyInterop.KeyFromVirtualKey((int)virtualKey);
        if (key == Key.None ||
            key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin)
        {
            return false;
        }

        var parts = new List<string>();
        if (IsVirtualKeyDown(0x11))
        {
            parts.Add("Ctrl");
        }

        if (IsVirtualKeyDown(0x12))
        {
            parts.Add("Alt");
        }

        if (IsVirtualKeyDown(0x10))
        {
            parts.Add("Shift");
        }

        if (IsVirtualKeyDown(0x5B) || IsVirtualKeyDown(0x5C))
        {
            parts.Add("Win");
        }

        parts.Add(NormalizeKeyName(key));
        binding = string.Join("+", parts);
        return true;
    }

    private static bool TryNormalizeKeyboardBinding(string? value, out string binding)
    {
        binding = string.Empty;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var modifiers = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        string? keyName = null;
        foreach (var rawPart in value.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var part = rawPart.Equals("Control", StringComparison.OrdinalIgnoreCase) ? "Ctrl" : rawPart;
            if (part.Equals("Ctrl", StringComparison.OrdinalIgnoreCase) ||
                part.Equals("Alt", StringComparison.OrdinalIgnoreCase) ||
                part.Equals("Shift", StringComparison.OrdinalIgnoreCase) ||
                part.Equals("Win", StringComparison.OrdinalIgnoreCase))
            {
                modifiers.Add(CanonicalModifier(part));
                continue;
            }

            if (!Enum.TryParse<Key>(part, ignoreCase: true, out var key) || key == Key.None)
            {
                return false;
            }

            keyName = NormalizeKeyName(key);
        }

        if (string.IsNullOrWhiteSpace(keyName))
        {
            return false;
        }

        var ordered = new[] { "Ctrl", "Alt", "Shift", "Win" }.Where(modifiers.Contains).Append(keyName);
        binding = string.Join("+", ordered);
        return true;
    }

    private static string CanonicalModifier(string modifier) =>
        modifier.Equals("Control", StringComparison.OrdinalIgnoreCase) || modifier.Equals("Ctrl", StringComparison.OrdinalIgnoreCase) ? "Ctrl" :
        modifier.Equals("Alt", StringComparison.OrdinalIgnoreCase) ? "Alt" :
        modifier.Equals("Shift", StringComparison.OrdinalIgnoreCase) ? "Shift" :
        "Win";

    private static string NormalizeKeyName(Key key) =>
        key switch
        {
            Key.Prior => "PageUp",
            Key.Next => "PageDown",
            Key.Return => "Enter",
            Key.Escape => "Esc",
            _ => key.ToString()
        };

    private void PollGamepads()
    {
        for (var index = 0; index < XInputMaxControllers; index++)
        {
            if (XInputGetState(index, out var state) != 0)
            {
                _previousGamepadConnected[index] = false;
                _previousGamepadStates[index] = default;
                continue;
            }

            var previousButtons = _previousGamepadConnected[index]
                ? _previousGamepadStates[index].Gamepad.Buttons
                : (ushort)0;
            var currentButtons = state.Gamepad.Buttons;
            var pressedButtons = (ushort)(currentButtons & ~previousButtons);
            if (pressedButtons != 0)
            {
                foreach (var button in GamepadButtons)
                {
                    if ((pressedButtons & button.Value) != 0)
                    {
                        GamepadButtonPressed?.Invoke(this, new GamepadButtonPressedEventArgs(index, button.Key));
                    }
                }

                if (SuppressActions)
                {
                    _previousGamepadConnected[index] = true;
                    _previousGamepadStates[index] = state;
                    continue;
                }

                foreach (var binding in _gamepadBindings)
                {
                    if ((binding.ControllerIndex is null || binding.ControllerIndex.Value == index) &&
                        (pressedButtons & binding.Button) != 0)
                    {
                        Dispatch(binding.Action);
                    }
                }
            }

            _previousGamepadConnected[index] = true;
            _previousGamepadStates[index] = state;
        }
    }

    private static bool TryParseGamepadBinding(string? value, string action, out GamepadBinding binding)
    {
        binding = default;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        int? controllerIndex = null;
        var buttonText = value.Trim();
        var parts = buttonText.Split(':', 2, StringSplitOptions.TrimEntries);
        if (parts.Length == 2)
        {
            buttonText = parts[1];
            var controller = parts[0].StartsWith("Gamepad", StringComparison.OrdinalIgnoreCase)
                ? parts[0]["Gamepad".Length..]
                : parts[0];
            if (!int.TryParse(controller, out var oneBasedIndex) || oneBasedIndex < 1 || oneBasedIndex > XInputMaxControllers)
            {
                return false;
            }

            controllerIndex = oneBasedIndex - 1;
        }

        if (!GamepadButtons.TryGetValue(buttonText, out var button))
        {
            return false;
        }

        binding = new GamepadBinding(action, controllerIndex, button);
        return true;
    }

    private void Dispatch(string action)
    {
        if (SuppressActions)
        {
            return;
        }

        if (_dispatcher.HasShutdownStarted || _dispatcher.HasShutdownFinished)
        {
            return;
        }

        _dispatcher.BeginInvoke(() => _executeAction(action), DispatcherPriority.Send);
    }

    private static bool IsVirtualKeyDown(int virtualKey) => (GetAsyncKeyState(virtualKey) & 0x8000) != 0;

    private static IntPtr SetKeyboardHook(LowLevelKeyboardProc proc)
    {
        using var process = Process.GetCurrentProcess();
        return SetWindowsHookEx(WhKeyboardLl, proc, GetModuleHandle(process.MainModule?.ModuleName), 0);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _gamepadTimer.Stop();
        if (_keyboardHook != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_keyboardHook);
            _keyboardHook = IntPtr.Zero;
        }
    }

    private delegate IntPtr LowLevelKeyboardProc(int code, IntPtr wParam, IntPtr lParam);

    private readonly record struct GamepadBinding(string Action, int? ControllerIndex, ushort Button);

    [StructLayout(LayoutKind.Sequential)]
    private struct KbdLlHookStruct
    {
        public uint VkCode;
        public uint ScanCode;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct XInputState
    {
        public uint PacketNumber;
        public XInputGamepad Gamepad;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct XInputGamepad
    {
        public ushort Buttons;
        public byte LeftTrigger;
        public byte RightTrigger;
        public short ThumbLX;
        public short ThumbLY;
        public short ThumbRX;
        public short ThumbRY;
    }

    private static readonly IReadOnlyDictionary<string, ushort> GamepadButtons =
        new Dictionary<string, ushort>(StringComparer.OrdinalIgnoreCase)
        {
            ["DPadUp"] = 0x0001,
            ["DPadDown"] = 0x0002,
            ["DPadLeft"] = 0x0004,
            ["DPadRight"] = 0x0008,
            ["Start"] = 0x0010,
            ["Back"] = 0x0020,
            ["LeftThumb"] = 0x0040,
            ["RightThumb"] = 0x0080,
            ["LeftShoulder"] = 0x0100,
            ["RightShoulder"] = 0x0200,
            ["A"] = 0x1000,
            ["B"] = 0x2000,
            ["X"] = 0x4000,
            ["Y"] = 0x8000
        };

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    [DllImport("xinput1_4.dll", EntryPoint = "XInputGetState")]
    private static extern uint XInputGetState(int dwUserIndex, out XInputState pState);
}

public sealed class GamepadButtonPressedEventArgs : EventArgs
{
    public GamepadButtonPressedEventArgs(int controllerIndex, string button)
    {
        ControllerIndex = controllerIndex;
        Button = button;
    }

    public int ControllerIndex { get; }
    public string Button { get; }
    public string BindingText => $"Gamepad{ControllerIndex + 1}:{Button}";
}

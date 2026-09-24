using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Interop;

namespace SshManager.Services;

/// <summary>
/// A system-wide shortcut (RegisterHotKey on a hidden message window) that brings up the main window.
/// Written as "Win+Alt+X", "Ctrl+Shift+F12"…; empty = off.
/// </summary>
public sealed class GlobalHotkey : IDisposable
{
    public const string Default = "Win+Alt+X";
    private const int WM_HOTKEY = 0x0312;
    private const int Id = 0x5353; // "SS"
    private const uint MOD_ALT = 0x1, MOD_CONTROL = 0x2, MOD_SHIFT = 0x4, MOD_WIN = 0x8, MOD_NOREPEAT = 0x4000;

    private readonly HwndSource _window;
    private readonly Action _pressed;
    private bool _registered;

    public GlobalHotkey(Action pressed)
    {
        _pressed = pressed;
        // HWND_MESSAGE parent: a window that only receives messages
        _window = new HwndSource(new HwndSourceParameters("SshManager.Hotkey") { ParentWindow = new IntPtr(-3), WindowStyle = 0 });
        _window.AddHook(WndProc);
    }

    /// <summary>The registered shortcut, null when off or taken.</summary>
    public string? Current { get; private set; }

    /// <summary>Registers <paramref name="text"/> (replacing the previous one). False: invalid or used by another program.</summary>
    public bool Set(string? text)
    {
        Unregister();
        if (string.IsNullOrWhiteSpace(text)) return true;
        if (Parse(text) is not var (mods, key)) return false;
        _registered = RegisterHotKey(_window.Handle, Id, mods | MOD_NOREPEAT, (uint)KeyInterop.VirtualKeyFromKey(key));
        Current = _registered ? Format(mods, key) : null;
        return _registered;
    }

    private void Unregister()
    {
        if (_registered) UnregisterHotKey(_window.Handle, Id);
        _registered = false;
        Current = null;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY && wParam.ToInt32() == Id)
        {
            handled = true;
            _pressed();
        }
        return IntPtr.Zero;
    }

    /// <summary>"Win+Alt+S" → modifiers and key; null when it has no modifier or no key.</summary>
    public static (uint Modifiers, Key Key)? Parse(string text)
    {
        uint mods = 0;
        Key? key = null;
        foreach (var raw in text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (raw.ToLowerInvariant())
            {
                case "ctrl" or "control": mods |= MOD_CONTROL; break;
                case "alt": mods |= MOD_ALT; break;
                case "shift": mods |= MOD_SHIFT; break;
                case "win" or "windows": mods |= MOD_WIN; break;
                default:
                    var name = raw.Length == 1 && char.IsDigit(raw[0]) ? "D" + raw : raw;
                    if (!Enum.TryParse<Key>(name, true, out var k) || IsModifier(k)) return null;
                    key = k;
                    break;
            }
        }
        return mods != 0 && key is { } kk ? (mods, kk) : null;
    }

    /// <summary>From a key press in the settings box: "Ctrl+Alt+K", null for a lone modifier.</summary>
    public static string? FromKeyPress(ModifierKeys modifiers, Key key, bool winDown)
    {
        if (IsModifier(key)) return null;
        uint mods = 0;
        if (modifiers.HasFlag(ModifierKeys.Control)) mods |= MOD_CONTROL;
        if (modifiers.HasFlag(ModifierKeys.Alt)) mods |= MOD_ALT;
        if (modifiers.HasFlag(ModifierKeys.Shift)) mods |= MOD_SHIFT;
        if (winDown || modifiers.HasFlag(ModifierKeys.Windows)) mods |= MOD_WIN;
        return mods == 0 ? null : Format(mods, key);
    }

    public static string Format(uint mods, Key key)
    {
        var parts = new List<string>();
        if ((mods & MOD_WIN) != 0) parts.Add("Win");
        if ((mods & MOD_CONTROL) != 0) parts.Add("Ctrl");
        if ((mods & MOD_ALT) != 0) parts.Add("Alt");
        if ((mods & MOD_SHIFT) != 0) parts.Add("Shift");
        var name = key.ToString();
        parts.Add(name.Length == 2 && name[0] == 'D' && char.IsDigit(name[1]) ? name[1..] : name);
        return string.Join("+", parts);
    }

    private static bool IsModifier(Key k) =>
        k is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin
            or Key.System or Key.None;

    public void Dispose()
    {
        Unregister();
        _window.Dispose();
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}

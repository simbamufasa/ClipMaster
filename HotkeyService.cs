using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace ClipMaster;

public class HotkeyService : IDisposable
{
    private const int WM_HOTKEY = 0x0312;
    private const int HOTKEY_ID = 0x1337;

    public const uint MOD_ALT      = 0x0001;
    public const uint MOD_CONTROL  = 0x0002;
    public const uint MOD_SHIFT    = 0x0004;
    public const uint MOD_WIN      = 0x0008;
    public const uint MOD_NOREPEAT = 0x4000;

    [DllImport("user32.dll")] private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    public event Action? HotkeyPressed;

    private HwndSource? _source;
    private IntPtr      _hwnd;

    public void Attach(Window window)
    {
        var helper = new WindowInteropHelper(window);
        helper.EnsureHandle();
        _hwnd   = helper.Handle;
        _source = HwndSource.FromHwnd(_hwnd);
        _source?.AddHook(WndProc);
    }

    public bool Register(string hotkeyString)
    {
        UnregisterHotKey(_hwnd, HOTKEY_ID);
        var (mods, vk) = ParseHotkey(hotkeyString);
        if (vk == 0) return false;
        return RegisterHotKey(_hwnd, HOTKEY_ID, mods | MOD_NOREPEAT, vk);
    }

    public void Unregister() => UnregisterHotKey(_hwnd, HOTKEY_ID);

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY && wParam.ToInt32() == HOTKEY_ID)
        {
            HotkeyPressed?.Invoke();
            handled = true;
        }
        return IntPtr.Zero;
    }

    /// <summary>
    /// Parses strings like "Ctrl+`", "CommandOrControl+`", "Alt+Shift+C", "Ctrl+F2".
    /// Returns (modifiers, virtualKeyCode). vk=0 means parse failure.
    /// </summary>
    public static (uint Mods, uint Vk) ParseHotkey(string hotkey)
    {
        uint mods = 0;
        uint vk   = 0;
        foreach (var raw in hotkey.Split('+'))
        {
            var p = raw.Trim().ToLowerInvariant();
            switch (p)
            {
                case "ctrl":
                case "control":
                case "commandorcontrol":
                case "command":
                    mods |= MOD_CONTROL; break;
                case "alt":
                    mods |= MOD_ALT;     break;
                case "shift":
                    mods |= MOD_SHIFT;   break;
                case "win":
                case "meta":
                    mods |= MOD_WIN;     break;
                case "`":
                case "oem_3":
                    vk = 0xC0; break; // VK_OEM_3
                default:
                    if (p.Length == 1)
                        vk = (uint)char.ToUpper(p[0]);
                    else if (p.StartsWith("f") && int.TryParse(p[1..], out var fn) && fn is >= 1 and <= 24)
                        vk = (uint)(0x6F + fn); // VK_F1=0x70
                    break;
            }
        }
        return (mods, vk);
    }

    public void Dispose()
    {
        if (_source == null) return;
        UnregisterHotKey(_hwnd, HOTKEY_ID);
        _source.RemoveHook(WndProc);
        _source = null;
    }
}

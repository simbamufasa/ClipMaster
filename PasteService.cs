using System.Runtime.InteropServices;
using System.Threading;

namespace ClipMaster;

public static class PasteService
{
    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint      type;
        public KEYBDINPUT ki;
        private ulong    _padding; // pad union to MOUSEINPUT size
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint   dwFlags;
        public uint   time;
        public IntPtr dwExtraInfo;
    }

    private const uint   INPUT_KEYBOARD  = 1;
    private const uint   KEYEVENTF_KEYUP = 0x0002;
    private const ushort VK_CONTROL      = 0x11;
    private const ushort VK_V            = 0x56;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    /// <summary>
    /// Simulates Ctrl+V via SendInput. Call this on a background thread AFTER hiding
    /// the ClipMaster window, so the previous window can regain focus.
    /// </summary>
    public static void Paste(int delayMs = 150)
    {
        Thread.Sleep(delayMs);
        var inputs = new INPUT[]
        {
            new() { type = INPUT_KEYBOARD, ki = new KEYBDINPUT { wVk = VK_CONTROL } },
            new() { type = INPUT_KEYBOARD, ki = new KEYBDINPUT { wVk = VK_V       } },
            new() { type = INPUT_KEYBOARD, ki = new KEYBDINPUT { wVk = VK_V,       dwFlags = KEYEVENTF_KEYUP } },
            new() { type = INPUT_KEYBOARD, ki = new KEYBDINPUT { wVk = VK_CONTROL, dwFlags = KEYEVENTF_KEYUP } },
        };
        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
    }
}

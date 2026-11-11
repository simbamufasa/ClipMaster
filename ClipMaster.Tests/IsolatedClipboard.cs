using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using Xunit;

namespace ClipMaster.Tests;

/// <summary>
/// A real Windows clipboard that is not the user's.
///
/// The clipboard belongs to a window station. This fixture moves the test process to a private,
/// invisible window station and runs all clipboard work on one dedicated STA thread whose
/// desktop lives in that station. Nothing the person at the keyboard has copied is read or
/// replaced, and no clipboard manager on their desktop (including a running ClipMaster) sees
/// test data. If any step of the isolation fails, <see cref="Isolated"/> is false and
/// <see cref="Run"/> refuses to run anything: tests then return without touching a clipboard.
///
/// Details that matter:
/// * The thread is created with CreateThread, not <see cref="Thread"/>. A .NET thread started as
///   STA already owns a COM window on the interactive desktop, which makes SetThreadDesktop fail;
///   one started without an apartment is MTA for good, and WPF's Clipboard needs STA. A native
///   thread can switch desktop first and become STA afterwards.
/// * Every raw clipboard call re-checks the window station first (<see cref="AssertIsolated"/>).
/// </summary>
public sealed class IsolatedClipboard : IDisposable
{
    public const string DesktopName = "ClipMasterTests";

    private const uint GENERIC_ALL    = 0x1000_0000;
    private const int  UOI_NAME       = 2;
    private const uint CF_UNICODETEXT = 13;
    private const uint GMEM_MOVEABLE  = 0x0002;

    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr GetProcessWindowStation();
    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowStationW(string? name, uint flags, uint access, IntPtr security);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetProcessWindowStation(IntPtr station);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool CloseWindowStation(IntPtr station);
    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateDesktopW(string name, string? device, IntPtr devmode, uint flags, uint access, IntPtr security);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool   CloseDesktop(IntPtr desktop);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool   SetThreadDesktop(IntPtr desktop);
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr GetThreadDesktop(uint threadId);
    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool GetUserObjectInformationW(IntPtr handle, int index, [Out] char[] buffer, uint bytes, out uint needed);

    [DllImport("user32.dll", SetLastError = true)] private static extern bool   OpenClipboard(IntPtr owner);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool   CloseClipboard();
    [DllImport("user32.dll", SetLastError = true)] private static extern bool   EmptyClipboard();
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetClipboardData(uint format, IntPtr handle);
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr GetClipboardData(uint format);
    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern uint RegisterClipboardFormatW(string name);

    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateThread(IntPtr security, UIntPtr stackSize, IntPtr start, IntPtr parameter, uint flags, out uint threadId);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint    WaitForSingleObject(IntPtr handle, uint milliseconds);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool    CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr  GlobalAlloc(uint flags, UIntPtr bytes);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr  GlobalFree(IntPtr handle);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr  GlobalLock(IntPtr handle);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool    GlobalUnlock(IntPtr handle);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern UIntPtr GlobalSize(IntPtr handle);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate uint ThreadStart(IntPtr parameter);

    private readonly BlockingCollection<Action> _work  = new();
    private readonly ManualResetEventSlim       _ready = new();
    private readonly ThreadStart _entry;      // kept alive for the native thread
    private IntPtr _original;
    private IntPtr _station;
    private IntPtr _desktop;
    private IntPtr _thread;
    private bool   _switched;

    /// <summary>True when clipboard work can run away from the user's clipboard.</summary>
    public bool Isolated { get; private set; }

    /// <summary>The window station the clipboard thread runs on (never WinSta0 when isolated).</summary>
    public string StationName { get; private set; } = "";

    /// <summary>Why isolation failed, when it did.</summary>
    public string SkipReason { get; private set; } = "";

    public IsolatedClipboard()
    {
        _entry = ThreadMain;
        try
        {
            _original = GetProcessWindowStation();
            // No name: Windows makes (or reopens) a non-interactive station for this logon session.
            _station = CreateWindowStationW(null, 0, GENERIC_ALL, IntPtr.Zero);
            if (_station == IntPtr.Zero) { SkipReason = $"CreateWindowStation failed ({Marshal.GetLastWin32Error()})"; return; }
            if (_station == _original)   { SkipReason = "the private window station is the current one"; return; }
            if (!SetProcessWindowStation(_station)) { SkipReason = $"SetProcessWindowStation failed ({Marshal.GetLastWin32Error()})"; return; }
            _switched = true;
            _desktop = CreateDesktopW(DesktopName, null, IntPtr.Zero, 0, GENERIC_ALL, IntPtr.Zero);
            if (_desktop == IntPtr.Zero) { SkipReason = $"CreateDesktop failed ({Marshal.GetLastWin32Error()})"; return; }

            _thread = CreateThread(IntPtr.Zero, UIntPtr.Zero, Marshal.GetFunctionPointerForDelegate(_entry), IntPtr.Zero, 0, out _);
            if (_thread == IntPtr.Zero) { SkipReason = $"CreateThread failed ({Marshal.GetLastWin32Error()})"; return; }
            if (!_ready.Wait(TimeSpan.FromSeconds(30))) { SkipReason = "the clipboard thread did not start"; Isolated = false; }
        }
        catch (Exception ex) { SkipReason = $"{ex.GetType().Name}: {ex.Message}"; Isolated = false; }
    }

    private uint ThreadMain(IntPtr parameter)
    {
        try
        {
            // First user32 call on this thread: before anything can tie it to the user's desktop.
            if (!SetThreadDesktop(_desktop))
            {
                SkipReason = $"SetThreadDesktop failed ({Marshal.GetLastWin32Error()})";
                return 1;
            }
            if (!Thread.CurrentThread.TrySetApartmentState(ApartmentState.STA))
            {
                SkipReason = "the clipboard thread could not become STA";
                return 1;
            }
            var problem = IsolationProblem();
            if (problem != null) { SkipReason = problem; return 1; }

            StationName = NameOf(GetProcessWindowStation());
            Isolated    = true;
            _ready.Set();

            foreach (var work in _work.GetConsumingEnumerable()) work();

            // Leave nothing behind: the station outlives this process.
            if (IsolationProblem() == null && OpenClipboard(IntPtr.Zero))
            {
                EmptyClipboard();
                CloseClipboard();
            }
            try { System.Windows.Threading.Dispatcher.CurrentDispatcher.InvokeShutdown(); } catch { /* best-effort */ }
            return 0;
        }
        catch (Exception ex)
        {
            SkipReason = $"{ex.GetType().Name}: {ex.Message}";
            Isolated   = false;
            return 1;
        }
        finally { _ready.Set(); }
    }

    private static string NameOf(IntPtr handle)
    {
        if (handle == IntPtr.Zero) return "";
        var buffer = new char[256];
        if (!GetUserObjectInformationW(handle, UOI_NAME, buffer, (uint)(buffer.Length * 2), out _)) return "";
        var end = Array.IndexOf(buffer, '\0');
        return new string(buffer, 0, end < 0 ? buffer.Length : end);
    }

    /// <summary>
    /// Null when the calling thread is certain to be using the private clipboard: the process is
    /// on a window station that is not the interactive one (always called WinSta0) and the thread
    /// is on the test desktop created inside it.
    /// </summary>
    private static string? IsolationProblem()
    {
        var station = NameOf(GetProcessWindowStation());
        if (station.Length == 0) return "could not read the window station name";
        if (station.Equals("WinSta0", StringComparison.OrdinalIgnoreCase)) return "still on the interactive window station WinSta0";
        var desktop = NameOf(GetThreadDesktop(GetCurrentThreadId()));
        if (desktop != DesktopName) return $"the thread is on desktop '{desktop}', not '{DesktopName}'";
        return null;
    }

    /// <summary>Throws unless the calling thread is on the private window station.</summary>
    public static void AssertIsolated()
    {
        var problem = IsolationProblem();
        if (problem != null) throw new InvalidOperationException($"Refusing to touch the clipboard: {problem}");
    }

    /// <summary>
    /// Runs clipboard work on the isolated STA thread and waits for it. Exceptions (including
    /// failed assertions) are rethrown on the caller. Refuses to run when isolation failed.
    /// </summary>
    public void Run(Action action)
    {
        if (!Isolated) throw new InvalidOperationException($"No isolated clipboard: {SkipReason}");
        ExceptionDispatchInfo? failure = null;
        using var done = new ManualResetEventSlim();
        _work.Add(() =>
        {
            try
            {
                AssertIsolated();
                action();
            }
            catch (Exception ex) { failure = ExceptionDispatchInfo.Capture(ex); }
            finally { done.Set(); }
        });
        done.Wait();
        failure?.Throw();
    }

    // ── Raw Win32 clipboard access, the way another program (Notes) uses it ──

    /// <summary>
    /// Replaces the clipboard with these formats as one item. Bytes are stored exactly as given
    /// (callers add terminators). Format names are registered names; "#13" style is not used:
    /// pass <see cref="UnicodeText"/> for CF_UNICODETEXT.
    /// </summary>
    public static void SetRaw(params (string Format, byte[] Bytes)[] formats)
    {
        AssertIsolated();
        if (!OpenClipboard(IntPtr.Zero)) throw new InvalidOperationException("OpenClipboard failed");
        try
        {
            if (!EmptyClipboard()) throw new InvalidOperationException("EmptyClipboard failed");
            foreach (var (format, bytes) in formats)
            {
                var block = GlobalAlloc(GMEM_MOVEABLE, (UIntPtr)(ulong)Math.Max(1, bytes.Length));
                if (block == IntPtr.Zero) throw new OutOfMemoryException();
                Marshal.Copy(bytes, 0, GlobalLock(block), bytes.Length);
                GlobalUnlock(block);
                if (SetClipboardData(FormatId(format), block) == IntPtr.Zero)
                {
                    GlobalFree(block);
                    throw new InvalidOperationException($"SetClipboardData({format}) failed ({Marshal.GetLastWin32Error()})");
                }
            }
        }
        finally { CloseClipboard(); }
    }

    /// <summary>The raw bytes of one format on the clipboard (the whole block), or null.</summary>
    public static byte[]? GetRaw(string format)
    {
        AssertIsolated();
        if (!OpenClipboard(IntPtr.Zero)) throw new InvalidOperationException("OpenClipboard failed");
        try
        {
            var handle = GetClipboardData(FormatId(format));
            if (handle == IntPtr.Zero) return null;
            var bytes  = new byte[(int)GlobalSize(handle).ToUInt64()];
            Marshal.Copy(GlobalLock(handle), bytes, 0, bytes.Length);
            GlobalUnlock(handle);
            return bytes;
        }
        finally { CloseClipboard(); }
    }

    public const string UnicodeText = "CF_UNICODETEXT";
    public const string Dib         = "CF_DIB";
    public const string HtmlFormat  = "HTML Format";

    private static uint FormatId(string format) => format switch
    {
        UnicodeText => CF_UNICODETEXT,
        Dib         => 8,
        _           => RegisterClipboardFormatW(format),
    };

    /// <summary>CF_UNICODETEXT bytes: UTF-16LE with a terminating NUL.</summary>
    public static byte[] Utf16(string text) => System.Text.Encoding.Unicode.GetBytes(text + "\0");

    /// <summary>Puts a Notes-style item on the clipboard: HTML Format (NUL-terminated) then text.</summary>
    public static void SetNotesItem(byte[] cfHtml, string? text)
    {
        var html = new byte[cfHtml.Length + 1];   // terminating NUL
        cfHtml.CopyTo(html, 0);
        if (text == null) SetRaw((HtmlFormat, html));
        else SetRaw((HtmlFormat, html), (UnicodeText, Utf16(text)));
    }

    /// <summary>The CF_UNICODETEXT on the clipboard up to its NUL, or null when there is none.</summary>
    public static string? GetText()
    {
        var raw = GetRaw(UnicodeText);
        if (raw == null) return null;
        var text = System.Text.Encoding.Unicode.GetString(raw, 0, raw.Length / 2 * 2);
        var end  = text.IndexOf('\0');
        return end < 0 ? text : text[..end];
    }

    public void Dispose()
    {
        _work.CompleteAdding();
        if (_thread != IntPtr.Zero)
        {
            WaitForSingleObject(_thread, 30_000);
            CloseHandle(_thread);
        }
        // Put the process back where it was before the other test classes or the host carry on.
        if (_switched && _original != IntPtr.Zero) SetProcessWindowStation(_original);
        if (_desktop != IntPtr.Zero) CloseDesktop(_desktop);
        if (_station != IntPtr.Zero && _station != _original) CloseWindowStation(_station);
        _work.Dispose();
        _ready.Dispose();
    }
}

/// <summary>
/// The process-wide window station switch must not overlap with any other test: this collection
/// never runs in parallel with another one.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class IsolatedClipboardCollection : ICollectionFixture<IsolatedClipboard>
{
    public const string Name = "Isolated clipboard";
}

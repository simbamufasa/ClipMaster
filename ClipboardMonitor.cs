using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace ClipMaster;

public class ClipboardMonitor : IDisposable
{
    private const int WM_CLIPBOARDUPDATE = 0x031D;
    private const int MaxRetries         = 4;

    [DllImport("user32.dll")] private static extern bool AddClipboardFormatListener(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool RemoveClipboardFormatListener(IntPtr hwnd);

    public event Action<string>?       NewClipText;
    public event Action<BitmapSource>? NewClipImage;
    public event Action<string, string>? NewClipRich;

    private HwndSource?     _source;
    private IntPtr          _hwnd;
    private DispatcherTimer? _retryTimer;   // non-blocking retry when the clipboard is locked

    public void Attach(Window window)
    {
        var helper = new WindowInteropHelper(window);
        helper.EnsureHandle();
        _hwnd   = helper.Handle;
        _source = HwndSource.FromHwnd(_hwnd);
        _source?.AddHook(WndProc);
        AddClipboardFormatListener(_hwnd);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_CLIPBOARDUPDATE)
            TryReadClipboard(attempt: 0);
        return IntPtr.Zero;
    }

    /// <summary>
    /// Reads the clipboard on the UI thread (required by WPF). If the source app still holds a
    /// lock (common with browser copy buttons / Snipping Tool), reschedules itself via a timer
    /// instead of blocking the UI thread with Thread.Sleep.
    /// </summary>
    private void TryReadClipboard(int attempt)
    {
        try
        {
            // Capture HTML together with its plain-text fallback as one composite item.
            // Plain browser text continues through the legacy text path unless the HTML
            // contains inline imagery (including image-bearing tables/fragments).
            if (System.Windows.Clipboard.ContainsText(System.Windows.TextDataFormat.Html))
            {
                var html = System.Windows.Clipboard.GetText(System.Windows.TextDataFormat.Html);
                if (!string.IsNullOrEmpty(html) && System.Text.RegularExpressions.Regex.IsMatch(html, @"<img\\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                {
                    var plain = System.Windows.Clipboard.ContainsText()
                        ? System.Windows.Clipboard.GetText() : "";
                    NewClipRich?.Invoke(plain, html);
                    return;
                }
            }
            if (System.Windows.Clipboard.ContainsText())
            {
                var text = System.Windows.Clipboard.GetText();
                if (!string.IsNullOrEmpty(text))
                    NewClipText?.Invoke(text);
            }
            else
            {
                // Text takes priority. Only check for images when no text is present.
                var image = TryGetClipboardImage();
                if (image != null)
                    NewClipImage?.Invoke(image);
            }
        }
        catch (COMException)
        {
            if (attempt < MaxRetries)
                ScheduleRetry(attempt + 1);
        }
        catch { /* unrecoverable — drop this update */ }
    }

    private void ScheduleRetry(int nextAttempt)
    {
        _retryTimer?.Stop();
        _retryTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(30) };
        _retryTimer.Tick += (_, _) =>
        {
            _retryTimer?.Stop();
            TryReadClipboard(nextAttempt);
        };
        _retryTimer.Start();
    }

    private static BitmapSource? TryGetClipboardImage()
    {
        // Prefer the "PNG" registered format — lossless, alpha preserved.
        // Used by Chrome, Edge, and Win+Shift+S Snipping Tool.
        // Let COMException propagate so the WndProc retry loop can handle it.
        if (System.Windows.Clipboard.ContainsData("PNG"))
        {
            var stream = System.Windows.Clipboard.GetData("PNG") as System.IO.MemoryStream;
            if (stream != null)
            {
                try
                {
                    stream.Position = 0;   // reset — GetData("PNG") may return stream at end
                    using (stream)
                    {
                        var decoder = new PngBitmapDecoder(
                            stream,
                            BitmapCreateOptions.PreservePixelFormat,
                            BitmapCacheOption.OnLoad);
                        return decoder.Frames[0];
                    }
                }
                catch { /* PNG decode failed — fall through to CF_DIB */ }
            }
        }
        // Fall back to CF_DIB (universal fallback — Print Screen, most apps)
        if (System.Windows.Clipboard.ContainsImage())
            return System.Windows.Clipboard.GetImage();
        return null;
    }

    public void Dispose()
    {
        _retryTimer?.Stop();
        _retryTimer = null;
        if (_source != null)
        {
            RemoveClipboardFormatListener(_hwnd);
            _source.RemoveHook(WndProc);
            _source = null;
        }
    }
}

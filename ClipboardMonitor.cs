using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace ClipMaster;

public enum ClipboardCaptureKind
{
    /// <summary>Nothing to capture (empty, unsupported, or ClipMaster's own write).</summary>
    None,
    Text,
    Image,
    /// <summary>HTML with images plus its plain text, as one item.</summary>
    Rich,
    /// <summary>Another program holds the clipboard; try again shortly.</summary>
    Busy,
}

public sealed record ClipboardCapture(
    ClipboardCaptureKind Kind,
    string?       Text  = null,
    BitmapSource? Image = null,
    RichPayload?  Rich  = null);

public class ClipboardMonitor : IDisposable
{
    private const int WM_CLIPBOARDUPDATE = 0x031D;
    private const int MaxRetries         = 4;

    [DllImport("user32.dll")] private static extern bool AddClipboardFormatListener(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool RemoveClipboardFormatListener(IntPtr hwnd);

    public event Action<string>?       NewClipText;
    public event Action<BitmapSource>? NewClipImage;
    public event Action<RichPayload>?  NewClipRich;

    /// <summary>
    /// Clipboard sequence number of a write ClipMaster made itself (restoring a rich clip). The
    /// update notification for that write is ignored, so pasting a clip does not capture it again.
    /// </summary>
    public uint? IgnoredSequence { get; set; }

    /// <summary>HTML larger than this is captured as plain text only.</summary>
    public int MaxRichHtmlBytes { get; set; } = RichClipboard.MaxHtmlBytes;

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
            var capture = Capture();
            if (capture.Kind == ClipboardCaptureKind.Busy)
            {
                if (attempt < MaxRetries) { ScheduleRetry(attempt + 1); return; }
                // Still locked for the rich read: do not lose the copy, take the text.
                capture = Capture(tryRich: false);
            }
            switch (capture.Kind)
            {
                case ClipboardCaptureKind.Rich:  NewClipRich?.Invoke(capture.Rich!);   break;
                case ClipboardCaptureKind.Text:  NewClipText?.Invoke(capture.Text!);   break;
                case ClipboardCaptureKind.Image: NewClipImage?.Invoke(capture.Image!); break;
            }
        }
        catch (COMException)
        {
            if (attempt < MaxRetries)
                ScheduleRetry(attempt + 1);
        }
        catch { /* unrecoverable — drop this update */ }
    }

    /// <summary>
    /// Decides what the clipboard holds right now and reads it. Must run on an STA thread (WPF).
    ///
    /// HTML that contains at least one image is captured together with its plain text as one
    /// rich item. Everything else — including HTML without images, HTML over the size limit and
    /// any failure in the rich read — goes down the legacy path unchanged: text first, then image.
    /// May throw COMException when the clipboard is locked (the caller retries).
    /// </summary>
    public ClipboardCapture Capture(bool tryRich = true)
    {
        if (IgnoredSequence is { } own && own != 0 && RichClipboard.CurrentSequence() == own)
            return new ClipboardCapture(ClipboardCaptureKind.None);

        if (tryRich)
        {
            var status = RichClipboard.TryRead(out var payload, MaxRichHtmlBytes);
            if (status == RichReadStatus.Rich) return new ClipboardCapture(ClipboardCaptureKind.Rich, Rich: payload);
            if (status == RichReadStatus.Busy) return new ClipboardCapture(ClipboardCaptureKind.Busy);
            if (status == RichReadStatus.TooLarge)
                TraceLog.Write("Rich clip over the size limit — capturing its plain text only");
        }

        if (System.Windows.Clipboard.ContainsText())
        {
            var text = System.Windows.Clipboard.GetText();
            if (!string.IsNullOrEmpty(text))
                return new ClipboardCapture(ClipboardCaptureKind.Text, Text: text);
        }
        else
        {
            // Text takes priority. Only check for images when no text is present.
            var image = TryGetClipboardImage();
            if (image != null)
                return new ClipboardCapture(ClipboardCaptureKind.Image, Image: image);
        }
        return new ClipboardCapture(ClipboardCaptureKind.None);
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

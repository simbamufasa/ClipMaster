using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace ClipMaster;

public class ClipboardMonitor : IDisposable
{
    private const int WM_CLIPBOARDUPDATE = 0x031D;

    [DllImport("user32.dll")] private static extern bool AddClipboardFormatListener(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool RemoveClipboardFormatListener(IntPtr hwnd);

    public event Action<string>?       NewClipText;
    public event Action<BitmapSource>? NewClipImage;

    private HwndSource? _source;
    private IntPtr      _hwnd;

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
        {
            // Clipboard may still be locked by the source app (e.g. browser copy buttons).
            // Retry a few times with a short delay to handle transient locks.
            for (int attempt = 0; attempt < 5; attempt++)
            {
                try
                {
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
                    break;
                }
                catch (COMException)
                {
                    if (attempt < 4)
                        Thread.Sleep(30);
                }
                catch { break; }
            }
        }
        return IntPtr.Zero;
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
        if (_source != null)
        {
            RemoveClipboardFormatListener(_hwnd);
            _source.RemoveHook(WndProc);
            _source = null;
        }
    }
}

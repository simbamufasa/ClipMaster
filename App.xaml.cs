using System.Drawing;
using System.IO;
using System.Windows;
using System.Windows.Forms;
using System.Windows.Media.Imaging;
using Application = System.Windows.Application;

namespace ClipMaster;

public partial class App : Application
{
    private NotifyIcon?      _tray;
    private TrayMenu?        _trayMenu;
    private MainWindow?      _window;
    private readonly DataService      _data   = new();
    private readonly ClipboardMonitor _clip   = new();
    private readonly HotkeyService    _hotkey = new();
    private AppData _db = new();
    private string  _lastClip = "";
    private bool    _suppressing;   // prevents re-entrant clipboard handling

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        DispatcherUnhandledException += (_, args) =>
        {
            TraceLog.Write($"UNHANDLED: {args.Exception}");
            args.Handled = true;
        };

        try { _db = _data.Load(); }
        catch (Exception ex)
        {
            TraceLog.Write($"Load FAILED: {ex}");
            _db = new AppData();
        }
        TraceLog.Write($"Startup: rules={_db.Rules.Count}");

        _window = new MainWindow(_db, _data);

        // Wire up Win32 services once the window handle exists.
        // SourceInitialized fires when EnsureHandle() or Show() creates the HWND.
        _window.SourceInitialized += (_, _) =>
        {
            try
            {
                _clip.Attach(_window);
                _hotkey.Attach(_window);

                _clip.NewClipText    += OnNewClip;
                _clip.NewClipImage   += OnNewClipImage;
                _hotkey.HotkeyPressed += ToggleWindow;

                // Register hotkey; fall back if taken
                var registered = _hotkey.Register(_db.Settings.Hotkey);
                if (!registered)
                {
                    const string fallback = "Ctrl+Shift+C";
                    if (_hotkey.Register(fallback))
                    {
                        _db.Settings.Hotkey = fallback;
                        _data.Save(_db);
                    }
                }
            }
            catch (Exception ex) { TraceLog.Write($"SourceInitialized FAILED: {ex}"); }
        };

        try { SetupTray(); TraceLog.Write("SetupTray OK"); }
        catch (Exception ex) { TraceLog.Write($"SetupTray FAILED: {ex}"); }

        // Create the HWND after the message loop starts so clipboard monitoring
        // begins at launch (without this, it only starts when the window is first shown).
        // Deferred via BeginInvoke so the tray icon appears immediately.
        Dispatcher.BeginInvoke(() =>
        {
            try
            {
                new System.Windows.Interop.WindowInteropHelper(_window).EnsureHandle();
                TraceLog.Write("EnsureHandle OK — clipboard monitor active");
            }
            catch (Exception ex) { TraceLog.Write($"EnsureHandle FAILED: {ex}"); }
        });

        // Sync startup setting with registry (installer may have set it)
        var registryHasStartup = StartupService.IsRunOnStartup();
        if (registryHasStartup && !_db.Settings.RunOnStartup)
        {
            _db.Settings.RunOnStartup = true;
            _data.Save(_db);
        }
        else if (!registryHasStartup && _db.Settings.RunOnStartup)
        {
            StartupService.SetRunOnStartup(true);
        }
    }

    private void SetupTray()
    {
        Icon icon;
        try
        {
            // Use PNG for proper transparency (ICO files had white corners)
            var uri    = new Uri("pack://application:,,,/Assets/icon-32.png");
            using var stream = GetResourceStream(uri)?.Stream;
            if (stream != null)
            {
                using var bmp = new Bitmap(stream);
                icon = Icon.FromHandle(bmp.GetHicon());
            }
            else
            {
                icon = (Icon)SystemIcons.Application.Clone();
            }
        }
        catch { icon = (Icon)SystemIcons.Application.Clone(); }

        _tray = new NotifyIcon
        {
            Icon    = icon,
            Text    = "ClipMaster",
            Visible = true,
        };

        // Custom WPF tray menu (no WinForms ContextMenuStrip)
        _trayMenu = new TrayMenu();
        _trayMenu.ShowRequested += ToggleWindow;
        _trayMenu.QuitRequested += Quit;

        _tray.MouseClick += (_, args) =>
        {
            if (args.Button == MouseButtons.Left)
                Dispatcher.Invoke(ToggleWindow);
            else if (args.Button == MouseButtons.Right)
                Dispatcher.Invoke(() => _trayMenu.ShowNearTray());
        };
    }

    public void ToggleWindow()
    {
        Dispatcher.Invoke(() =>
        {
            if (_window == null) return;
            if (_window.IsVisible)
            {
                _window.Hide();
            }
            else
            {
                _window.PositionNearCursor();
                _window.Show();
                _window.Activate();
                _window.RefreshClips(_db.Clips);
            }
        });
    }

    public void RehostHotkey(string newHotkey)
    {
        _hotkey.Register(newHotkey);
    }

    private void OnNewClip(string text)
    {
        if (_suppressing) return;
        if (text == _lastClip) return;
        _lastClip = text;

        var (finalText, wasTransformed) = _data.AddClip(_db, text);

        if (wasTransformed)
        {
            _lastClip = finalText;
            _suppressing = true;
            try { Dispatcher.Invoke(() => System.Windows.Clipboard.SetText(finalText)); }
            finally { _suppressing = false; }
        }

        Dispatcher.Invoke(() =>
        {
            if (_window?.IsVisible == true)
                _window.RefreshClips(_db.Clips);
        });
    }

    private void OnNewClipImage(BitmapSource image)
    {
        if (_suppressing) return;
        // Freeze before crossing the thread boundary — BitmapSource is a DispatcherObject
        // and cannot be accessed from a non-owning thread without freezing first.
        if (image.CanFreeze) image.Freeze();
        Task.Run(() =>
        {
            var entry = _data.AddImageClip(_db, image);
            if (entry == null) return;
            Dispatcher.Invoke(() =>
            {
                if (_window?.IsVisible == true)
                    _window.RefreshClips(_db.Clips);
            });
        });
    }

    public void OnPasteAndPromote(string clipId)
    {
        var clip = _db.Clips.FirstOrDefault(c => c.Id == clipId);
        if (clip == null) return;

        _data.PromoteClip(_db, clipId);
        var text = !string.IsNullOrEmpty(clip.Text) ? clip.Text : clip.Raw;
        if (string.IsNullOrEmpty(text)) return;

        if (!TrySetClipboardAndHide(text)) return;
        Task.Run(() => PasteService.Paste(150));
    }

    public void OnPasteClip(string clipId)
    {
        var clip = _db.Clips.FirstOrDefault(c => c.Id == clipId);
        if (clip == null) return;

        var text = !string.IsNullOrEmpty(clip.Text) ? clip.Text : clip.Raw;
        if (string.IsNullOrEmpty(text)) return;

        if (!TrySetClipboardAndHide(text)) return;
        Task.Run(() => PasteService.Paste(150));
    }

    public void OnPasteImageClip(string clipId)
    {
        var clip = _db.Clips.FirstOrDefault(c => c.Id == clipId && c.IsImage);
        if (clip?.ImagePath == null) return;
        if (!TrySetClipboardImageAndHide(clip.ImagePath)) return;
        Task.Run(() => PasteService.Paste(150));
    }

    public void OnCopyImageClip(string clipId)
    {
        var clip = _db.Clips.FirstOrDefault(c => c.Id == clipId && c.IsImage);
        if (clip?.ImagePath == null) return;
        try
        {
            var abs = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".clipmaster", clip.ImagePath);
            var bmp = new BitmapImage(new Uri(abs));
            _suppressing = true;
            try { Dispatcher.Invoke(() => System.Windows.Clipboard.SetImage(bmp)); }
            finally { _suppressing = false; }
        }
        catch (Exception ex) { TraceLog.Write($"OnCopyImageClip FAILED: {ex}"); }
    }

    private bool TrySetClipboardAndHide(string text)
    {
        _lastClip = text;
        _suppressing = true;
        try
        {
            Dispatcher.Invoke(() =>
            {
                for (int attempt = 0; attempt < 5; attempt++)
                {
                    try
                    {
                        System.Windows.Clipboard.SetText(text);
                        _window?.Hide();
                        return;
                    }
                    catch (System.Runtime.InteropServices.COMException)
                    {
                        if (attempt < 4) Thread.Sleep(30);
                    }
                }
                TraceLog.Write("SetText failed after 5 attempts — clipboard locked");
            });
            return true;
        }
        catch (Exception ex)
        {
            TraceLog.Write($"TrySetClipboardAndHide FAILED: {ex}");
            return false;
        }
        finally { _suppressing = false; }
    }

    private bool TrySetClipboardImageAndHide(string relPath)
    {
        try
        {
            var abs = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".clipmaster", relPath);
            var bmp = new BitmapImage(new Uri(abs));
            var success = false;
            _suppressing = true;
            try
            {
                Dispatcher.Invoke(() =>
                {
                    for (int attempt = 0; attempt < 5; attempt++)
                    {
                        try
                        {
                            System.Windows.Clipboard.SetImage(bmp);
                            _window?.Hide();
                            success = true;
                            return;
                        }
                        catch (System.Runtime.InteropServices.COMException)
                        {
                            if (attempt < 4) Thread.Sleep(30);
                        }
                    }
                    TraceLog.Write("SetImage failed after 5 attempts — clipboard locked");
                });
            }
            finally { _suppressing = false; }
            return success;
        }
        catch (Exception ex)
        {
            TraceLog.Write($"TrySetClipboardImageAndHide FAILED: {ex}");
            return false;
        }
    }

    private void Quit()
    {
        _tray?.Dispose();
        _clip.Dispose();
        _hotkey.Dispose();
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _tray?.Dispose();
        _clip.Dispose();
        _hotkey.Dispose();
        base.OnExit(e);
    }
}

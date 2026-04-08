using System.Drawing;
using System.Windows;
using System.Windows.Forms;
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

        _db = _data.Load();

        _window = new MainWindow(_db, _data);

        // Attach Win32 services after the window handle exists
        _window.SourceInitialized += (_, _) =>
        {
            _clip.Attach(_window);
            _hotkey.Attach(_window);

            _clip.NewClipText    += OnNewClip;
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
        };

        SetupTray();

        // Sync startup registry with setting
        StartupService.SetRunOnStartup(_db.Settings.RunOnStartup);

        // Don't show window on startup — live in tray only
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

    public void OnPasteAndPromote(string clipId)
    {
        var clip = _db.Clips.FirstOrDefault(c => c.Id == clipId);
        if (clip == null) return;

        _data.PromoteClip(_db, clipId);
        var text = !string.IsNullOrEmpty(clip.Text) ? clip.Text : clip.Raw;
        if (string.IsNullOrEmpty(text)) return;

        _lastClip = text;
        _suppressing = true;
        try
        {
            Dispatcher.Invoke(() =>
            {
                System.Windows.Clipboard.SetText(text);
                _window?.Hide();
            });
        }
        finally { _suppressing = false; }

        Task.Run(() => PasteService.Paste(150));
    }

    public void OnPasteClip(string clipId)
    {
        var clip = _db.Clips.FirstOrDefault(c => c.Id == clipId);
        if (clip == null) return;

        var text = !string.IsNullOrEmpty(clip.Text) ? clip.Text : clip.Raw;
        if (string.IsNullOrEmpty(text)) return;

        _lastClip = text;
        _suppressing = true;
        try
        {
            Dispatcher.Invoke(() =>
            {
                System.Windows.Clipboard.SetText(text);
                _window?.Hide();
            });
        }
        finally { _suppressing = false; }

        Task.Run(() => PasteService.Paste(150));
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

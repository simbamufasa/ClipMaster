using System.Drawing;
using System.Windows;
using System.Windows.Forms;
using Application = System.Windows.Application;

namespace ClipMaster;

public partial class App : Application
{
    private NotifyIcon?      _tray;
    private MainWindow?      _window;
    private readonly DataService      _data   = new();
    private readonly ClipboardMonitor _clip   = new();
    private readonly HotkeyService    _hotkey = new();
    private AppData _db = new();
    private string  _lastClip = "";

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

        // Don't show window on startup — live in tray only
    }

    private void SetupTray()
    {
        Icon icon;
        try
        {
            var uri    = new Uri("pack://application:,,,/Assets/icon.ico");
            var stream = GetResourceStream(uri)?.Stream;
            icon = stream != null ? new Icon(stream) : SystemIcons.Application;
        }
        catch { icon = SystemIcons.Application; }

        _tray = new NotifyIcon
        {
            Icon    = icon,
            Text    = "ClipMaster",
            Visible = true,
        };

        var menu = new ContextMenuStrip();
        menu.Items.Add("Show ClipMaster", null, (_, _) => ToggleWindow());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Quit", null, (_, _) => Quit());
        _tray.ContextMenuStrip = menu;

        _tray.MouseClick += (_, args) =>
        {
            if (args.Button == MouseButtons.Left) ToggleWindow();
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
        if (text == _lastClip) return;
        _lastClip = text;

        var (finalText, wasTransformed) = _data.AddClip(_db, text);

        if (wasTransformed)
        {
            _lastClip = finalText;
            Dispatcher.Invoke(() => System.Windows.Clipboard.SetText(finalText));
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
        _lastClip = clip.Text;

        Dispatcher.Invoke(() =>
        {
            System.Windows.Clipboard.SetText(clip.Text);
            _window?.Hide();
        });

        Task.Run(() => PasteService.Paste(150));
    }

    public void OnPasteClip(string clipId)
    {
        var clip = _db.Clips.FirstOrDefault(c => c.Id == clipId);
        if (clip == null) return;

        _lastClip = clip.Text;
        Dispatcher.Invoke(() =>
        {
            System.Windows.Clipboard.SetText(clip.Text);
            _window?.Hide();
        });

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

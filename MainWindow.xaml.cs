using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

// Aliases to resolve WPF vs WinForms ambiguity (project uses both)
using WpfApp         = System.Windows.Application;
using WpfBrush       = System.Windows.Media.Brush;
using WpfBrushes     = System.Windows.Media.Brushes;
using WpfButton      = System.Windows.Controls.Button;
using WpfCheckBox    = System.Windows.Controls.CheckBox;
using WpfCursors     = System.Windows.Input.Cursors;
using WpfFontFamily  = System.Windows.Media.FontFamily;
using WpfKeyArgs     = System.Windows.Input.KeyEventArgs;
using WpfMessageBox  = System.Windows.MessageBox;
using WpfOrientation = System.Windows.Controls.Orientation;
using WpfTextBox     = System.Windows.Controls.TextBox;

namespace ClipMaster;

public partial class MainWindow : Window
{
    private readonly DataService _svc;
    private AppData _db;

    private string  _search    = "";
    private string  _filter    = "all";
    private string? _tagFilter;
    private string  _activeTab = "stack";

    private DispatcherTimer? _toastTimer;
    private DispatcherTimer? _resizeSaveTimer;
    private bool _dialogOpen;

    public MainWindow(AppData db, DataService svc)
    {
        _db  = db;
        _svc = svc;
        InitializeComponent();
    }

    // ── Tab navigation ──────────────────────────────────────────────────────

    private void Tab_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton btn) return;
        // Guard: fired during InitializeComponent before the full visual tree exists
        if (ClipList == null) return;
        _activeTab = (string)btn.Tag;

        foreach (var tb in new[] { TabStack, TabRules, TabSettings })
            if (tb != btn) tb.IsChecked = false;

        StackScrollView.Visibility    = _activeTab == "stack"    ? Visibility.Visible : Visibility.Collapsed;
        RulesScrollView.Visibility    = _activeTab == "rules"    ? Visibility.Visible : Visibility.Collapsed;
        SettingsScrollView.Visibility = _activeTab == "settings" ? Visibility.Visible : Visibility.Collapsed;

        SearchBox.Visibility   = _activeTab == "stack" ? Visibility.Visible : Visibility.Collapsed;
        PasteTopBtn.Visibility = _activeTab == "stack" ? Visibility.Visible : Visibility.Collapsed;

        if (_activeTab == "stack")    RenderStack();
        if (_activeTab == "rules")    RenderRules();
        if (_activeTab == "settings") RenderSettings();
    }

    private void FilterChip_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton btn) return;
        // Guard: fired during InitializeComponent before the full visual tree exists
        if (ClipList == null) return;
        _filter = (string)btn.Tag;
        foreach (var tb in new[] { FilterAll, FilterPinned, FilterSensitive })
            if (tb != btn) tb.IsChecked = false;
        RenderStack();
    }

    // ── Window mechanics ────────────────────────────────────────────────────

    private void TitleBar_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed) DragMove();
    }

    private void CloseBtn_Click(object sender, RoutedEventArgs e) => Hide();

    private void Window_Deactivated(object sender, EventArgs e)
    {
        if (IsVisible && !_dialogOpen) Hide();
    }

    private void Window_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        _db.Settings.WindowWidth  = (int)e.NewSize.Width;
        _db.Settings.WindowHeight = (int)e.NewSize.Height;
        _resizeSaveTimer?.Stop();
        _resizeSaveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _resizeSaveTimer.Tick += (_, _) => { _svc.Save(_db); _resizeSaveTimer.Stop(); };
        _resizeSaveTimer.Start();
    }

    public void PositionNearCursor()
    {
        var cursor = System.Windows.Forms.Cursor.Position;
        var screen = System.Windows.Forms.Screen.FromPoint(cursor);
        var bounds = screen.WorkingArea;
        var w = (int)Width;
        var h = (int)Height;
        Left = Math.Max(bounds.Left, Math.Min(cursor.X - w / 2, bounds.Right  - w));
        Top  = Math.Max(bounds.Top,  Math.Min(cursor.Y - h / 2, bounds.Bottom - h));
    }

    // ── Keyboard shortcuts ──────────────────────────────────────────────────

    private void Window_KeyDown(object sender, WpfKeyArgs e)
    {
        if (e.Key == Key.Escape) { Hide(); e.Handled = true; return; }

        if (_activeTab == "stack")
        {
            if (e.Key == Key.Enter && !SearchBox.IsFocused)
            {
                e.Handled = true; PasteTop(); return;
            }
            if (e.Key == Key.V && Keyboard.Modifiers.HasFlag(ModifierKeys.Control) && !SearchBox.IsFocused)
            {
                e.Handled = true; PasteTop(); return;
            }
            if (e.Key == Key.F && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
            {
                e.Handled = true; SearchBox.Focus(); SearchBox.SelectAll(); return;
            }
        }
    }

    // ── Search ──────────────────────────────────────────────────────────────

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        _search = SearchBox.Text;
        RenderStack();
    }

    private void SearchBox_KeyDown(object sender, WpfKeyArgs e)
    {
        if (e.Key == Key.Escape)
        {
            SearchBox.Clear();
            MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
        }
    }

    // ── Public refresh ──────────────────────────────────────────────────────

    public void RefreshClips(List<ClipEntry> clips)
    {
        _db.Clips = clips;
        if (_activeTab == "stack") RenderStack();
    }

    // ── Paste top ───────────────────────────────────────────────────────────

    private void PasteTopBtn_Click(object sender, RoutedEventArgs e) => PasteTop();

    private void PasteTop()
    {
        var clips = GetFilteredClips();
        if (clips.Count == 0) return;
        ((App)WpfApp.Current).OnPasteAndPromote(clips[0].Id);
    }

    // ── Filter logic ────────────────────────────────────────────────────────

    private List<ClipEntry> GetFilteredClips()
    {
        IEnumerable<ClipEntry> clips = _db.Clips;
        clips = _filter switch
        {
            "pinned"    => clips.Where(c => c.Pinned),
            "sensitive" => clips.Where(c => c.IsSensitive),
            _           => clips,
        };
        if (_tagFilter != null)
            clips = clips.Where(c => c.Tags.Contains(_tagFilter));
        if (!string.IsNullOrWhiteSpace(_search))
        {
            var q = _search.ToLowerInvariant();
            // Image clips have no text content — hide them entirely during search
            clips = clips.Where(c =>
                !c.IsImage &&
                (c.Text.ToLowerInvariant().Contains(q) ||
                 c.Raw.ToLowerInvariant().Contains(q)));
        }
        var list     = clips.ToList();
        var pinned   = list.Where(c => c.Pinned).ToList();
        var unpinned = list.Where(c => !c.Pinned).ToList();
        return [.. pinned, .. unpinned];
    }

    // ── Stack render ─────────────────────────────────────────────────────────

    private void RenderStack()
    {
        ClipList.Children.Clear();
        var clips = GetFilteredClips();
        for (int i = 0; i < clips.Count; i++)
        {
            var card = clips[i].IsImage
                ? BuildImageCard(clips[i], i + 1)
                : BuildClipCard(clips[i], i + 1);
            ClipList.Children.Add(card);
        }
        RenderTagFilterChips();
    }

    private void RenderTagFilterChips()
    {
        TagFilterChips.Children.Clear();
        var allTags = _db.Clips.SelectMany(c => c.Tags).Distinct().OrderBy(t => t).ToList();
        foreach (var tag in allTags)
        {
            var t   = tag;
            var btn = new ToggleButton
            {
                Content   = $"# {t}",
                IsChecked = _tagFilter == t,
                Style     = (Style)FindResource("TabBtn"),
                Margin    = new Thickness(0, 0, 4, 4),
                FontSize  = 11,
            };
            btn.Checked   += (_, _) => { _tagFilter = t; RenderStack(); };
            btn.Unchecked += (_, _) => { if (_tagFilter == t) { _tagFilter = null; RenderStack(); } };
            TagFilterChips.Children.Add(btn);
        }
    }

    private static string TimeAgo(long ts)
    {
        var s = (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - ts) / 1000;
        if (s < 60)  return $"{s}s ago";
        var m = s / 60;
        if (m < 60)  return $"{m}m ago";
        var h = m / 60;
        if (h < 24)  return $"{h}h ago";
        return $"{h / 24}d ago";
    }

    private static WpfFontFamily AppFont  => new("Segoe UI Variable, Segoe UI, sans-serif");
    private static WpfFontFamily MonoFont => new("Cascadia Code, Consolas, monospace");

    private UIElement BuildClipCard(ClipEntry clip, int rank)
    {
        double opacity = rank switch { 1 => 1.0, 2 => 0.85, 3 => 0.70, _ => 0.55 };
        string rankBg  = rank switch { 1 => "#7c6af7", 2 => "#5548aa", 3 => "#3d3880", _ => "#2a2460" };

        // Rank badge
        var rankBadge = new Border
        {
            Style      = (Style)FindResource("RankBadge"),
            Background = BrushFromHex(rankBg),
            Child      = new TextBlock
            {
                Text                = rank.ToString(),
                FontSize            = 10,
                FontWeight          = FontWeights.Bold,
                Foreground          = WpfBrushes.White,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                VerticalAlignment   = VerticalAlignment.Center,
            },
        };

        // Status badges
        var badgesPanel = new StackPanel
        {
            Orientation       = WpfOrientation.Horizontal,
            Margin            = new Thickness(6, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        if (clip.Pinned)                  badgesPanel.Children.Add(MakeBadge("pinned",      "#3d3880", "#a89ef7"));
        if (clip.IsSensitive)             badgesPanel.Children.Add(MakeBadge("sensitive",   "#4d1f1f", "#e05252"));
        if (clip.AppliedRules?.Count > 0) badgesPanel.Children.Add(MakeBadge("transformed", "#1a3a2a", "#4caf82"));

        // Tag pills
        var tagsPanel = new StackPanel
        {
            Orientation       = WpfOrientation.Horizontal,
            Margin            = new Thickness(4, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        foreach (var t in clip.Tags)
        {
            tagsPanel.Children.Add(new Border
            {
                Style = (Style)FindResource("TagPill"),
                Child = new TextBlock
                {
                    Text              = t,
                    FontSize          = 9,
                    Foreground        = (WpfBrush)FindResource("AccentBrush"),
                    VerticalAlignment = VerticalAlignment.Center,
                },
            });
        }

        var metaTb = new TextBlock
        {
            Text              = TimeAgo(clip.CopiedAt),
            FontSize          = 10,
            Foreground        = (WpfBrush)FindResource("Text3Brush"),
            Margin            = new Thickness(6, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };

        var header = new WrapPanel { Margin = new Thickness(0, 0, 0, 4) };
        header.Children.Add(rankBadge);
        header.Children.Add(badgesPanel);
        header.Children.Add(tagsPanel);
        header.Children.Add(metaTb);

        // Clip text
        var displayText = !string.IsNullOrEmpty(clip.Text) ? clip.Text : clip.Raw;
        var clipTextTb  = new TextBlock
        {
            Text         = clip.IsSensitive ? "••••••••" : displayText,
            FontSize     = 12,
            FontFamily   = AppFont,
            Foreground   = (WpfBrush)FindResource("Text1Brush"),
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxHeight    = 72,
            Opacity      = opacity,
            Margin       = new Thickness(0, 2, 0, 8),
            TextWrapping = TextWrapping.Wrap,
            LineHeight   = 18,
        };
        if (clip.IsSensitive)
        {
            clipTextTb.MouseEnter += (_, _) => { clipTextTb.Text = displayText; clipTextTb.Opacity = 0.7; };
            clipTextTb.MouseLeave += (_, _) => { clipTextTb.Text = "••••••••";  clipTextTb.Opacity = opacity; };
        }

        // Action buttons
        var actions = new WrapPanel { Margin = new Thickness(0, 4, 0, 0) };

        var pasteBtn = CardBtn("Paste", "PasteBtn");
        var copyBtn  = CardBtn("Copy",  "ActionBtn");
        var pinBtn   = CardBtn(clip.Pinned ? "Unpin" : "Pin", "ActionBtn");
        var tagBtn   = CardBtn("Tag",   "ActionBtn");
        var delBtn   = CardBtn("✕",     "DangerBtn");

        pasteBtn.Click += (_, _) => ((App)WpfApp.Current).OnPasteClip(clip.Id);
        copyBtn.Click  += (_, _) =>
        {
            System.Windows.Clipboard.SetText(displayText);
            ShowToast("Copied");
        };
        pinBtn.Click += (_, _) =>
        {
            clip.Pinned = !clip.Pinned;
            _svc.Save(_db);
            RenderStack();
        };
        tagBtn.Click += (_, _) => OpenTagDialog(clip);
        delBtn.Click += (_, _) =>
        {
            _db.Clips.Remove(clip);
            _svc.Save(_db);
            RenderStack();
        };

        actions.Children.Add(pasteBtn);
        actions.Children.Add(copyBtn);
        actions.Children.Add(pinBtn);
        actions.Children.Add(tagBtn);

        // Manual rule buttons
        foreach (var rule in _db.Rules.Where(r => r.Enabled && r.Mode == "manual"))
        {
            var r   = rule;
            var btn = CardBtn(r.Name, "ActionBtn");
            btn.Click += (_, _) =>
            {
                try
                {
                    clip.Text = _svc.ApplyRule(clip.Text, r);
                    clip.AppliedRules ??= [];
                    if (!clip.AppliedRules.Contains(r.Name)) clip.AppliedRules.Add(r.Name);
                    _svc.Save(_db);
                    RenderStack();
                    ShowToast("Rule applied");
                }
                catch (Exception ex) { ShowToast($"Rule error: {ex.Message}"); }
            };
            actions.Children.Add(btn);
        }
        actions.Children.Add(delBtn);

        var cardContent = new StackPanel();
        cardContent.Children.Add(header);
        cardContent.Children.Add(clipTextTb);
        cardContent.Children.Add(actions);

        var card = new Border
        {
            Style   = (Style)FindResource("ClipCard"),
            Opacity = opacity,
            Child   = cardContent,
        };

        // Card hover effect
        card.MouseEnter += (_, _) => card.Background = BrushFromHex("#282828");
        card.MouseLeave += (_, _) => card.Background = (WpfBrush)FindResource("SurfaceBrush");

        // Double-click on card body (not buttons) → paste and promote
        card.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ClickCount == 2 && e.OriginalSource is not WpfButton)
                ((App)WpfApp.Current).OnPasteAndPromote(clip.Id);
        };

        // Right-click context menu
        card.MouseRightButtonUp += (_, _) =>
        {
            var menu = new ContextMenu();
            MenuItem MI(string hdr, Action action)
            {
                var mi = new MenuItem { Header = hdr };
                mi.Click += (_, _) => action();
                return mi;
            }
            menu.Items.Add(MI("Paste",        () => ((App)WpfApp.Current).OnPasteClip(clip.Id)));
            menu.Items.Add(MI("Copy text",    () => { System.Windows.Clipboard.SetText(displayText); ShowToast("Copied"); }));
            menu.Items.Add(MI(clip.Pinned ? "Unpin" : "Pin", () => { clip.Pinned = !clip.Pinned; _svc.Save(_db); RenderStack(); }));
            menu.Items.Add(MI("Manage tags…", () => OpenTagDialog(clip)));
            menu.Items.Add(new Separator());
            menu.Items.Add(MI("Delete",       () => { _db.Clips.Remove(clip); _svc.Save(_db); RenderStack(); }));
            card.ContextMenu = menu;
            menu.IsOpen      = true;
        };

        return card;
    }

    private UIElement BuildImageCard(ClipEntry clip, int rank)
    {
        double opacity = rank switch { 1 => 1.0, 2 => 0.85, 3 => 0.70, _ => 0.55 };
        string rankBg  = rank switch { 1 => "#7c6af7", 2 => "#5548aa", 3 => "#3d3880", _ => "#2a2460" };

        // Rank badge
        var rankBadge = new Border
        {
            Style      = (Style)FindResource("RankBadge"),
            Background = BrushFromHex(rankBg),
            Child      = new TextBlock
            {
                Text                = rank.ToString(),
                FontSize            = 10,
                FontWeight          = FontWeights.Bold,
                Foreground          = WpfBrushes.White,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                VerticalAlignment   = VerticalAlignment.Center,
            },
        };

        // Badges row
        var badgesPanel = new StackPanel
        {
            Orientation       = WpfOrientation.Horizontal,
            Margin            = new Thickness(6, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        badgesPanel.Children.Add(MakeBadge("image", "#1a2e3a", "#5bb8d4"));
        if (clip.Pinned) badgesPanel.Children.Add(MakeBadge("pinned", "#3d3880", "#a89ef7"));

        var metaTb = new TextBlock
        {
            Text              = TimeAgo(clip.CopiedAt),
            FontSize          = 10,
            Foreground        = (WpfBrush)FindResource("Text3Brush"),
            Margin            = new Thickness(6, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };

        var header = new StackPanel
        {
            Orientation = WpfOrientation.Horizontal,
            Margin      = new Thickness(0, 0, 0, 8),
        };
        header.Children.Add(rankBadge);
        header.Children.Add(badgesPanel);
        header.Children.Add(metaTb);

        // Thumbnail — loaded with DecodePixelWidth so WPF only decodes a scaled version
        System.Windows.Controls.Image thumbnail;
        if (clip.ImagePath != null)
        {
            var abs = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".clipmaster", clip.ImagePath);
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.UriSource        = new Uri(abs);
            bmp.DecodePixelWidth = 420;
            bmp.CacheOption      = BitmapCacheOption.OnLoad;
            bmp.EndInit();
            thumbnail = new System.Windows.Controls.Image
            {
                Source              = bmp,
                Height              = 140,
                Stretch             = System.Windows.Media.Stretch.UniformToFill,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch,
            };
        }
        else
        {
            thumbnail = new System.Windows.Controls.Image { Height = 140 };
        }

        var thumbBorder = new Border
        {
            CornerRadius = new CornerRadius(6),
            ClipToBounds = true,
            Margin       = new Thickness(0, 0, 0, 8),
            Child        = thumbnail,
        };

        // Action buttons
        var pasteBtn = CardBtn("Paste", "PasteBtn");
        var copyBtn  = CardBtn("Copy",  "ActionBtn");
        var pinBtn   = CardBtn(clip.Pinned ? "Unpin" : "Pin", "ActionBtn");
        var tagBtn   = CardBtn("Tag",   "ActionBtn");
        var delBtn   = CardBtn("✕",     "DangerBtn");

        pasteBtn.Click += (_, _) => ((App)WpfApp.Current).OnPasteImageClip(clip.Id);
        copyBtn.Click  += (_, _) =>
        {
            ((App)WpfApp.Current).OnCopyImageClip(clip.Id);
            ShowToast("Copied");
        };
        pinBtn.Click += (_, _) =>
        {
            clip.Pinned = !clip.Pinned;
            _svc.Save(_db);
            RenderStack();
        };
        tagBtn.Click += (_, _) => OpenTagDialog(clip);
        delBtn.Click += (_, _) =>
        {
            _db.Clips.Remove(clip);
            _svc.DeleteImageFile(clip.ImagePath);
            _svc.Save(_db);
            RenderStack();
        };

        var actionsPanel = new StackPanel { Orientation = WpfOrientation.Horizontal };
        actionsPanel.Children.Add(pasteBtn);
        actionsPanel.Children.Add(copyBtn);
        actionsPanel.Children.Add(pinBtn);
        actionsPanel.Children.Add(tagBtn);
        actionsPanel.Children.Add(delBtn);

        // Dimensions / size label
        var sizeLabel = clip.ImageBytes >= 1_048_576
            ? $"{clip.ImageBytes / 1_048_576.0:F1} MB"
            : $"{clip.ImageBytes / 1024.0:F0} KB";
        var dimLabel = new TextBlock
        {
            Text              = $"{clip.ImageWidth} × {clip.ImageHeight} · PNG · {sizeLabel}",
            FontSize          = 9,
            Foreground        = (WpfBrush)FindResource("Text3Brush"),
            VerticalAlignment = VerticalAlignment.Center,
        };

        // Footer: actions left, dimensions right
        var footer = new DockPanel { LastChildFill = false };
        DockPanel.SetDock(dimLabel, Dock.Right);
        footer.Children.Add(actionsPanel);
        footer.Children.Add(dimLabel);

        var cardContent = new StackPanel();
        cardContent.Children.Add(header);
        cardContent.Children.Add(thumbBorder);
        cardContent.Children.Add(footer);

        var card = new Border
        {
            Style   = (Style)FindResource("ClipCard"),
            Opacity = opacity,
            Child   = cardContent,
        };

        card.MouseEnter += (_, _) => card.Background = BrushFromHex("#282828");
        card.MouseLeave += (_, _) => card.Background = (WpfBrush)FindResource("SurfaceBrush");

        // Double-click on card body → paste
        card.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ClickCount == 2 && e.OriginalSource is not WpfButton)
                ((App)WpfApp.Current).OnPasteImageClip(clip.Id);
        };

        // Right-click context menu
        card.MouseRightButtonUp += (_, _) =>
        {
            var menu = new ContextMenu();
            MenuItem MI(string hdr, Action action)
            {
                var mi = new MenuItem { Header = hdr };
                mi.Click += (_, _) => action();
                return mi;
            }
            menu.Items.Add(MI("Paste", () => ((App)WpfApp.Current).OnPasteImageClip(clip.Id)));
            menu.Items.Add(MI("Copy",  () => { ((App)WpfApp.Current).OnCopyImageClip(clip.Id); ShowToast("Copied"); }));
            menu.Items.Add(MI(clip.Pinned ? "Unpin" : "Pin", () => { clip.Pinned = !clip.Pinned; _svc.Save(_db); RenderStack(); }));
            menu.Items.Add(MI("Manage tags…", () => OpenTagDialog(clip)));
            menu.Items.Add(new Separator());
            menu.Items.Add(MI("Delete", () =>
            {
                _db.Clips.Remove(clip);
                _svc.DeleteImageFile(clip.ImagePath);
                _svc.Save(_db);
                RenderStack();
            }));
            card.ContextMenu = menu;
            menu.IsOpen      = true;
        };

        return card;
    }

    private static Border MakeBadge(string text, string bgHex, string fgHex) =>
        new()
        {
            Background   = BrushFromHex(bgHex),
            CornerRadius = new CornerRadius(8),
            Padding      = new Thickness(7, 2, 7, 2),
            Margin       = new Thickness(0, 0, 4, 0),
            Child        = new TextBlock
            {
                Text       = text,
                FontSize   = 9,
                FontFamily = new WpfFontFamily("Segoe UI Variable, Segoe UI"),
                FontWeight = FontWeights.Medium,
                Foreground = BrushFromHex(fgHex),
            },
        };

    private WpfButton CardBtn(string label, string styleKey) =>
        new()
        {
            Content = label,
            Style   = (Style)FindResource(styleKey),
            Margin  = new Thickness(0, 0, 4, 0),
            Padding = new Thickness(6, 3, 6, 3),
        };

    private static SolidColorBrush BrushFromHex(string hex) =>
        (SolidColorBrush)new BrushConverter().ConvertFrom(hex)!;

    private WpfTextBox StyledInput(string text, double width = 160) =>
        new()
        {
            Text            = text,
            Width           = width,
            FontSize        = 12,
            FontFamily      = AppFont,
            Padding         = new Thickness(8, 5, 8, 5),
            Background      = (WpfBrush)FindResource("SurfaceBrush"),
            Foreground      = (WpfBrush)FindResource("Text1Brush"),
            BorderBrush     = (WpfBrush)FindResource("BorderBrush"),
            BorderThickness = new Thickness(1),
            CaretBrush      = (WpfBrush)FindResource("AccentBrush"),
        };

    // ── Toast ───────────────────────────────────────────────────────────────

    public void ShowToast(string msg)
    {
        ToastText.Text      = msg;
        ToastBorder.Opacity = 1;
        _toastTimer?.Stop();
        _toastTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.8) };
        _toastTimer.Tick += (_, _) => { ToastBorder.Opacity = 0; _toastTimer.Stop(); };
        _toastTimer.Start();
    }

    // ── Tag dialog ───────────────────────────────────────────────────────────
    private void OpenTagDialog(ClipEntry clip)
    {
        _dialogOpen = true;
        try
        {
            var dlg = new TagDialog(_db, _svc, clip) { Owner = this };
            if (dlg.ShowDialog() == true)
                RenderStack();
        }
        finally { _dialogOpen = false; }
    }

    // ── Rules ────────────────────────────────────────────────────────────────
    private void RenderRules()
    {
        // Keep the Add Rule button (first child), remove rest
        while (RulesList.Children.Count > 1)
            RulesList.Children.RemoveAt(1);

        foreach (var rule in _db.Rules)
            RulesList.Children.Add(BuildRuleCard(rule));
    }

    private UIElement BuildRuleCard(Rule rule)
    {
        var nameTb = new TextBlock
        {
            Text       = rule.Name,
            FontSize   = 13,
            FontWeight = FontWeights.SemiBold,
            Foreground = (WpfBrush)FindResource("Text1Brush"),
            VerticalAlignment = VerticalAlignment.Center,
        };

        var modeBg = rule.Mode == "auto" ? "#1a3a2a" : "#2a2460";
        var modeFg = rule.Mode == "auto" ? "#4caf82" : "#a89ef7";
        var badge  = MakeBadge(rule.Mode, modeBg, modeFg);

        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };
        DockPanel.SetDock(badge, Dock.Right);
        header.Children.Add(badge);
        header.Children.Add(nameTb);

        var patternTb = new TextBlock
        {
            Text         = $"/{rule.Pattern}/{rule.Flags}  →  {(string.IsNullOrEmpty(rule.Replacement) ? "(delete)" : rule.Replacement)}",
            FontSize     = 11,
            FontFamily   = MonoFont,
            Foreground   = (WpfBrush)FindResource("Text2Brush"),
            Margin       = new Thickness(0, 2, 0, 8),
            TextWrapping = TextWrapping.Wrap,
        };

        var enabledCb = new WpfCheckBox
        {
            Content   = "Enabled",
            IsChecked = rule.Enabled,
            Foreground = (WpfBrush)FindResource("Text2Brush"),
            FontSize  = 11,
            Margin    = new Thickness(0, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        enabledCb.Checked   += (_, _) => { rule.Enabled = true;  _svc.Save(_db); };
        enabledCb.Unchecked += (_, _) => { rule.Enabled = false; _svc.Save(_db); };

        var editBtn = CardBtn("Edit",   "ActionBtn");
        var delBtn  = CardBtn("Delete", "DangerBtn");

        editBtn.Click += (_, _) =>
        {
            _dialogOpen = true;
            try
            {
                var dlg = new RuleDialog(rule) { Owner = this };
                if (dlg.ShowDialog() == true)
                {
                    var idx = _db.Rules.IndexOf(rule);
                    if (idx >= 0) _db.Rules[idx] = dlg.Result;
                    _svc.Save(_db);
                    RenderRules();
                }
            }
            finally { _dialogOpen = false; }
        };
        delBtn.Click += (_, _) =>
        {
            _db.Rules.Remove(rule);
            _svc.Save(_db);
            RenderRules();
        };

        var btnRow = new WrapPanel { Margin = new Thickness(0, 4, 0, 0) };
        btnRow.Children.Add(enabledCb);
        btnRow.Children.Add(editBtn);
        btnRow.Children.Add(delBtn);

        var content = new StackPanel { Margin = new Thickness(10) };
        content.Children.Add(header);
        content.Children.Add(patternTb);
        content.Children.Add(btnRow);

        return new Border
        {
            Background      = (WpfBrush)FindResource("SurfaceBrush"),
            BorderBrush     = (WpfBrush)FindResource("BorderSubtleBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius    = new CornerRadius(10),
            Margin          = new Thickness(0, 0, 0, 6),
            Child           = content,
        };
    }

    private void AddRule_Click(object sender, RoutedEventArgs e)
    {
        _dialogOpen = true;
        try
        {
            var dlg = new RuleDialog { Owner = this };
            if (dlg.ShowDialog() == true)
            {
                _db.Rules.Add(dlg.Result);
                _svc.Save(_db);
                RenderRules();
            }
        }
        finally { _dialogOpen = false; }
    }

    // ── Settings ─────────────────────────────────────────────────────────────
    private void RenderSettings()
    {
        SettingsForm.Children.Clear();
        var s = _db.Settings;

        void SectionHeader(string title)
        {
            SettingsForm.Children.Add(new TextBlock
            {
                Text       = title,
                FontSize   = 10,
                FontFamily = AppFont,
                FontWeight = FontWeights.SemiBold,
                Foreground = (WpfBrush)FindResource("Text3Brush"),
                Margin     = new Thickness(0, 14, 0, 8),
            });
        }

        void SettingRow(string label, string desc, UIElement control)
        {
            var lbl  = new TextBlock { Text = label, FontSize = 13, FontFamily = AppFont, Foreground = (WpfBrush)FindResource("Text1Brush") };
            var dsc  = new TextBlock { Text = desc,  FontSize = 11, FontFamily = AppFont, Foreground = (WpfBrush)FindResource("Text2Brush"), Margin = new Thickness(0, 2, 0, 0) };
            var left = new StackPanel();
            left.Children.Add(lbl);
            left.Children.Add(dsc);
            var row = new DockPanel { Margin = new Thickness(0, 0, 0, 10), LastChildFill = true };
            DockPanel.SetDock(control, Dock.Right);
            row.Children.Add(control);
            row.Children.Add(left);
            SettingsForm.Children.Add(row);
        }

        // General
        SectionHeader("GENERAL");

        var hotkeyBox = StyledInput(s.Hotkey);
        SettingRow("Hotkey", "Global shortcut to open/close", hotkeyBox);

        var maxBox = StyledInput(s.MaxHistory.ToString(), 80);
        SettingRow("Max History", "Unpinned clips to keep", maxBox);

        var startupCb = new WpfCheckBox { IsChecked = s.RunOnStartup, Foreground = (WpfBrush)FindResource("Text1Brush") };
        SettingRow("Run on startup", "Launch ClipMaster when Windows starts", startupCb);

        // Privacy
        SectionHeader("PRIVACY");

        var maskCb = new WpfCheckBox { IsChecked = s.MaskPasswords, Foreground = (WpfBrush)FindResource("Text1Brush") };
        SettingRow("Mask sensitive content", "Blur passwords & tokens", maskCb);

        // Rules
        SectionHeader("RULES");

        var autoRulesCb = new WpfCheckBox { IsChecked = s.AutoApplyRules, Foreground = (WpfBrush)FindResource("Text1Brush") };
        SettingRow("Auto-apply rules on copy", "Run \"auto\" rules when something is copied", autoRulesCb);

        // Tags
        SectionHeader("TAGS");

        var tagsWrap = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
        foreach (var tag in _db.Tags)
        {
            var t    = tag;
            var pill = new Border
            {
                Background   = BrushFromHex("#2a2460"),
                CornerRadius = new CornerRadius(4),
                Padding      = new Thickness(6, 2, 4, 2),
                Margin       = new Thickness(0, 0, 4, 4),
            };
            var row = new StackPanel { Orientation = WpfOrientation.Horizontal };
            row.Children.Add(new TextBlock
            {
                Text = t, FontSize = 11, Foreground = (WpfBrush)FindResource("AccentBrush"),
                VerticalAlignment = VerticalAlignment.Center,
            });
            var x = new WpfButton
            {
                Content = "✕", Background = WpfBrushes.Transparent, BorderThickness = new Thickness(0),
                Foreground = (WpfBrush)FindResource("Text3Brush"), FontSize = 9,
                Padding = new Thickness(3, 0, 0, 0), Cursor = WpfCursors.Hand,
            };
            x.Click += (_, _) =>
            {
                _db.Tags.Remove(t);
                _db.Clips.ForEach(c => c.Tags.Remove(t));
                _svc.Save(_db);
                RenderSettings();
                RenderStack();
            };
            row.Children.Add(x);
            pill.Child = row;
            tagsWrap.Children.Add(pill);
        }
        SettingsForm.Children.Add(tagsWrap);

        var newTagBox = StyledInput("");
        var addTagBtn = CardBtn("Add Tag", "ActionBtn");
        void DoAddTag()
        {
            var raw = newTagBox.Text.Trim().ToLowerInvariant().Replace(" ", "-");
            if (string.IsNullOrEmpty(raw) || _db.Tags.Contains(raw)) { newTagBox.Clear(); return; }
            _db.Tags.Add(raw);
            _svc.Save(_db);
            newTagBox.Clear();
            RenderSettings();
            RenderStack();
        }
        addTagBtn.Click += (_, _) => DoAddTag();
        newTagBox.KeyDown += (_, e) => { if (e.Key == Key.Enter) DoAddTag(); };

        var addTagRow = new StackPanel { Orientation = WpfOrientation.Horizontal, Margin = new Thickness(0, 0, 0, 10) };
        addTagRow.Children.Add(newTagBox);
        addTagRow.Children.Add(addTagBtn);
        SettingsForm.Children.Add(addTagRow);

        // Data
        SectionHeader("DATA");

        var clearBtn = CardBtn("Clear History", "DangerBtn");
        clearBtn.Click += (_, _) =>
        {
            _dialogOpen = true;
            try
            {
                if (WpfMessageBox.Show("Clear all unpinned history? Cannot be undone.",
                    "Confirm", MessageBoxButton.YesNo) == MessageBoxResult.Yes)
                {
                    _db.Clips = _db.Clips.Where(c => c.Pinned).ToList();
                    _svc.Save(_db);
                    RenderStack();
                    ShowToast("History cleared");
                }
            }
            finally { _dialogOpen = false; }
        };
        SettingRow("Clear clip history", "Removes all unpinned clips permanently", clearBtn);

        // Save button
        var saveBtn = new WpfButton
        {
            Content = "Save Settings", Style = (Style)FindResource("PrimaryBtn"),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0),
        };
        saveBtn.Click += (_, _) =>
        {
            s.Hotkey         = hotkeyBox.Text.Trim();
            s.MaxHistory     = int.TryParse(maxBox.Text, out var m) ? m : 500;
            s.MaskPasswords  = maskCb.IsChecked == true;
            s.AutoApplyRules = autoRulesCb.IsChecked == true;
            s.RunOnStartup   = startupCb.IsChecked == true;
            _svc.Save(_db);
            ((App)WpfApp.Current).RehostHotkey(s.Hotkey);
            StartupService.SetRunOnStartup(s.RunOnStartup);
            ShowToast("Settings saved");
        };
        SettingsForm.Children.Add(saveBtn);
    }
}

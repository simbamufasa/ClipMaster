using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

// Aliases to resolve WPF vs WinForms ambiguity (project uses both via global usings)
using WpfCheckBox = System.Windows.Controls.CheckBox;
using WpfColor    = System.Windows.Media.Color;
using WpfKeyArgs  = System.Windows.Input.KeyEventArgs;

namespace ClipMaster;

public partial class TagDialog : Window
{
    private readonly AppData     _db;
    private readonly DataService _svc;
    private readonly ClipEntry   _clip;
    private readonly HashSet<string> _currentTags;

    public TagDialog(AppData db, DataService svc, ClipEntry clip)
    {
        _db          = db;
        _svc         = svc;
        _clip        = clip;
        _currentTags = new HashSet<string>(clip.Tags);
        InitializeComponent();
        Render();
    }

    private void Render()
    {
        TagList.Children.Clear();
        foreach (var tag in _db.Tags)
        {
            var t  = tag;
            var cb = new WpfCheckBox
            {
                Content   = t,
                IsChecked = _currentTags.Contains(t),
                Foreground = new SolidColorBrush(WpfColor.FromRgb(0xf0, 0xf0, 0xf0)),
                FontSize  = 13,
                Margin    = new Thickness(0, 4, 0, 4),
            };
            cb.Checked   += (_, _) => _currentTags.Add(t);
            cb.Unchecked += (_, _) => _currentTags.Remove(t);
            TagList.Children.Add(cb);
        }
    }

    private void AddTagBtn_Click(object sender, RoutedEventArgs e) => CreateTag();

    private void NewTagBox_KeyDown(object sender, WpfKeyArgs e)
    {
        if (e.Key == Key.Enter) CreateTag();
    }

    private void CreateTag()
    {
        var raw = NewTagBox.Text.Trim().ToLowerInvariant().Replace(" ", "-");
        if (string.IsNullOrEmpty(raw) || _db.Tags.Contains(raw))
        { NewTagBox.Clear(); return; }
        _db.Tags.Add(raw);
        _currentTags.Add(raw);
        _svc.Save(_db);
        NewTagBox.Clear();
        Render();
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        _clip.Tags   = [.. _currentTags];
        _svc.Save(_db);
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void Window_KeyDown(object sender, WpfKeyArgs e)
    {
        if (e.Key == Key.Escape) DialogResult = false;
    }
}

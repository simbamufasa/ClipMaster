using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

// Aliases to resolve WPF vs WinForms ambiguity (project uses both via global usings)
using WpfColor      = System.Windows.Media.Color;
using WpfKeyArgs    = System.Windows.Input.KeyEventArgs;
using WpfMessageBox = System.Windows.MessageBox;

namespace ClipMaster;

public partial class RuleDialog : Window
{
    public Rule Result { get; private set; }

    public RuleDialog(Rule? existing = null)
    {
        Result = existing != null
            ? new Rule { Id = existing.Id, Enabled = existing.Enabled }
            : new Rule { Id = $"rule_{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}", Enabled = true };

        InitializeComponent();

        if (existing != null)
        {
            TitleLabel.Text     = "Edit Rule";
            NameBox.Text        = existing.Name;
            PatternBox.Text     = existing.Pattern;
            FlagsBox.Text       = existing.Flags;
            ReplacementBox.Text = existing.Replacement;
            foreach (ComboBoxItem item in ModeBox.Items)
                if ((string)item.Tag == existing.Mode) { item.IsSelected = true; break; }
        }
    }

    private void LiveTest_Changed(object sender, TextChangedEventArgs e)
    {
        var pattern = PatternBox.Text;
        var test    = TestBox.Text;
        if (string.IsNullOrEmpty(pattern) || string.IsNullOrEmpty(test))
        { TestResult.Visibility = Visibility.Collapsed; return; }

        try
        {
            var flags = FlagsBox.Text;
            var opts  = RegexOptions.None;
            if (flags.Contains('i')) opts |= RegexOptions.IgnoreCase;
            if (flags.Contains('m')) opts |= RegexOptions.Multiline;
            var result = new Regex(pattern, opts).Replace(test, ReplacementBox.Text ?? "");
            TestResult.Text       = result.Length > 0 ? result : "(empty result)";
            TestResult.Foreground = new SolidColorBrush(WpfColor.FromRgb(0x4c, 0xaf, 0x82));
            TestResult.Visibility = Visibility.Visible;
        }
        catch (Exception ex)
        {
            TestResult.Text       = $"Error: {ex.Message}";
            TestResult.Foreground = new SolidColorBrush(WpfColor.FromRgb(0xe0, 0x52, 0x52));
            TestResult.Visibility = Visibility.Visible;
        }
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(NameBox.Text))    { WpfMessageBox.Show("Name is required");    return; }
        if (string.IsNullOrWhiteSpace(PatternBox.Text)) { WpfMessageBox.Show("Pattern is required"); return; }
        try { _ = new Regex(PatternBox.Text); }
        catch (Exception ex) { WpfMessageBox.Show($"Invalid regex: {ex.Message}"); return; }

        var modeItem = (ComboBoxItem)ModeBox.SelectedItem;
        Result.Name        = NameBox.Text.Trim();
        Result.Pattern     = PatternBox.Text.Trim();
        Result.Flags       = string.IsNullOrWhiteSpace(FlagsBox.Text) ? "g" : FlagsBox.Text.Trim();
        Result.Replacement = ReplacementBox.Text ?? "";
        Result.Mode        = (string)modeItem.Tag;
        DialogResult       = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void Window_KeyDown(object sender, WpfKeyArgs e)
    {
        if (e.Key == Key.Escape) DialogResult = false;
    }
}

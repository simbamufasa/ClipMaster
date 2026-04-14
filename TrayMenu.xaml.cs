using System.Windows;
using System.Windows.Media;
using WpfMouseArgs    = System.Windows.Input.MouseEventArgs;
using WpfMouseBtnArgs = System.Windows.Input.MouseButtonEventArgs;

namespace ClipMaster;

public partial class TrayMenu : Window
{
    public event Action? ShowRequested;
    public event Action? ExportRequested;
    public event Action? QuitRequested;

    public TrayMenu()
    {
        InitializeComponent();
    }

    public void ShowNearTray()
    {
        var cursor = System.Windows.Forms.Cursor.Position;
        var screen = System.Windows.Forms.Screen.FromPoint(cursor);
        var work   = screen.WorkingArea;

        Measure(new System.Windows.Size(double.PositiveInfinity, double.PositiveInfinity));
        var w = DesiredSize.Width  > 0 ? DesiredSize.Width  : 200;
        var h = DesiredSize.Height > 0 ? DesiredSize.Height : 100;

        Left = Math.Min(cursor.X, work.Right  - w - 8);
        Top  = Math.Min(cursor.Y - h, work.Bottom - h - 8);

        Show();
        Activate();
    }

    private void Window_Deactivated(object sender, EventArgs e) => Hide();

    private void ShowItem_Click(object sender, WpfMouseBtnArgs e)
    {
        Hide();
        ShowRequested?.Invoke();
    }

    private void ExportItem_Click(object sender, WpfMouseBtnArgs e)
    {
        Hide();
        ExportRequested?.Invoke();
    }

    private void QuitItem_Click(object sender, WpfMouseBtnArgs e)
    {
        Hide();
        QuitRequested?.Invoke();
    }

    private static readonly SolidColorBrush HoverBg =
        new(System.Windows.Media.Color.FromArgb(0x20, 0xFF, 0xFF, 0xFF));

    private void Item_MouseEnter(object sender, WpfMouseArgs e)
    {
        if (sender is System.Windows.Controls.Border b)
            b.Background = HoverBg;
    }

    private void Item_MouseLeave(object sender, WpfMouseArgs e)
    {
        if (sender is System.Windows.Controls.Border b)
            b.Background = System.Windows.Media.Brushes.Transparent;
    }
}

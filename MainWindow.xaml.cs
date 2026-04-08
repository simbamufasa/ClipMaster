using System.Windows;

namespace ClipMaster;

public partial class MainWindow : Window
{
    public MainWindow(AppData db, DataService svc)
    {
        InitializeComponent();
    }

    public void PositionNearCursor() { }
    public void RefreshClips(List<ClipEntry> clips) { }
}

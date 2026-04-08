using System.Windows;

// Disambiguate: both WPF and WinForms export 'Application'
using Application = System.Windows.Application;

namespace ClipMaster;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var window = new MainWindow();
        window.Show();
    }
}

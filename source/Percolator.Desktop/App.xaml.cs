using System.Windows;
using Percolator.Desktop.Main;

namespace Percolator.Desktop;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    private void App_OnStartup(object sender, StartupEventArgs e)
    {
        var mainWindow = new MainWindow();
        mainWindow.DataContext = new MainWindowViewmodel();
        mainWindow.Show();
    }
}
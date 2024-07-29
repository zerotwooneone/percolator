using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Percolator.Desktop.Main;

namespace Percolator.Desktop;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    private void App_OnStartup(object sender, StartupEventArgs e)
    {
        var serviceCollection = new ServiceCollection();
        serviceCollection.AddScoped<MainWindowViewmodel>();
        
        var serviceProvider = serviceCollection.BuildServiceProvider();
        
        var mainWindow = new MainWindow();
        mainWindow.DataContext = serviceProvider.GetRequiredService<MainWindowViewmodel>();
        mainWindow.Show();
    }
}
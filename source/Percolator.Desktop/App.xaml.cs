using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Percolator.Desktop.Main;
using Percolator.Desktop.Udp;
using R3;

namespace Percolator.Desktop;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    private void App_OnStartup(object sender, StartupEventArgs e)
    {
        //todo: wire up error handling to logging
        //WpfProviderInitializer.SetDefaultObservableSystem();
        
        var serviceCollection = new ServiceCollection();
        serviceCollection.AddScoped<MainWindowViewmodel>();
        serviceCollection.AddSingleton<MainService>();
        serviceCollection.AddSingleton<UdpClientFactory>();
        
        var serviceProvider = serviceCollection.BuildServiceProvider();
        
        var mainWindow = new MainWindow();
        mainWindow.DataContext = serviceProvider.GetRequiredService<MainWindowViewmodel>();
        mainWindow.Show();
    }
}
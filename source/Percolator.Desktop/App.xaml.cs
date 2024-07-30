using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
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
        var builder = Host.CreateDefaultBuilder()
            .ConfigureServices(services =>
            {
                services.AddSingleton<MainWindow>();
                services.AddScoped<MainWindowViewmodel>();
                services.AddSingleton<MainService>();
                services.AddSingleton<UdpClientFactory>();
                
            });
        //todo: wire up error handling to logging
        //WpfProviderInitializer.SetDefaultObservableSystem();
        
        var host = builder.Build();
        
        var mainWindow = host.Services.GetRequiredService<MainWindow>();
        mainWindow.DataContext = host.Services.GetRequiredService<MainWindowViewmodel>();
        mainWindow.Show();
    }
}
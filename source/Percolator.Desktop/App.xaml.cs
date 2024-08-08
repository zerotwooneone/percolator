using System.Windows;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Percolator.Desktop.Crypto;
using Percolator.Desktop.Data;
using Percolator.Desktop.Main;
using Percolator.Desktop.Udp;
using R3;

namespace Percolator.Desktop;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    private ILogger<App> _logger;

    private void App_OnStartup(object sender, StartupEventArgs e)
    {
        WpfProviderInitializer.SetDefaultObservableSystem(UnhandledExceptionHandler);
        var builder = Host.CreateDefaultBuilder()
            .ConfigureServices(services =>
            {
                services.AddSingleton<MainWindow>();
                services.AddScoped<MainWindowViewmodel>();
                services.AddSingleton<MainService>();
                services.AddSingleton<IAnnouncerService>(p=>p.GetRequiredService<MainService>());
                services.AddSingleton<IChatService>(p=>p.GetRequiredService<MainService>());
                services.AddSingleton<IAnnouncerInitializer>(p=>p.GetRequiredService<MainService>());
                services.AddSingleton<UdpClientFactory>();
                services.AddSingleton<SelfEncryptionService>(s =>
                    new SelfEncryptionService("6e3c367d-380c-4a0d-8b66-ad397fbac2d9")); //todo: get id from config
                services.AddSingleton<ViewmodelFactory>();
                services.AddSingleton<IAnnouncerViewmodelFactory>(p=>p.GetRequiredService<ViewmodelFactory>());
                services.AddSingleton<IChatViewmodelFactory>(p=>p.GetRequiredService<ViewmodelFactory>());
                services.AddHostedService<SqliteService>();
                services.AddSingleton<IPersistenceService,SqliteService2>();
                services.AddDbContext<ApplicationDbContext>(options =>
                {
                    options.UseSqlite("Data Source=percolator.db");
                });
            });
        
        var host = builder.Build();
        _logger = host.Services.GetRequiredService<ILogger<App>>();
        
        Task.Factory.StartNew(() => host.StartAsync().Wait());
        
        var mainWindow = host.Services.GetRequiredService<MainWindow>();
        mainWindow.DataContext = host.Services.GetRequiredService<MainWindowViewmodel>();
        mainWindow.Show();
    }

    private void UnhandledExceptionHandler(Exception ex)
    {
        _logger.LogError(ex, "Unhandled exception in app");
    }
}
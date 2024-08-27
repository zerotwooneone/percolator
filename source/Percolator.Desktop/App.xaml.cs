﻿using System.Security.Cryptography;
using System.Text;
using System.Windows;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Percolator.Crypto;
using Percolator.Crypto.Grpc;
using Percolator.Desktop.Crypto;
using Percolator.Desktop.Data;
using Percolator.Desktop.Domain.Chat;
using Percolator.Desktop.Domain.Client;
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
                services.AddSingleton<SettingsViewmodel>();
                services.AddSingleton<MainDefaultViewmodel>();
                services.AddScoped<MainWindowViewmodel>();
                services.AddSingleton<MainService>();
                services.AddSingleton<IRemoteClientService>(p => p.GetRequiredService<MainService>());
                services.AddSingleton<IChatService>(p => p.GetRequiredService<MainService>());

                services.AddSingleton<RemoteClientRepository>();
                services.AddSingleton<IRemoteClientRepository>(p => p.GetRequiredService<RemoteClientRepository>());
                services.AddSingleton<IRemoteClientInitializer>(p => p.GetRequiredService<RemoteClientRepository>());

                services.AddSingleton<UdpClientFactory>();
                services.AddSingleton<SelfEncryptionService>();
                services.AddSingleton<ViewmodelFactory>();
                services.AddSingleton<IRemoteClientViewmodelFactory>(p => p.GetRequiredService<ViewmodelFactory>());
                services.AddSingleton<IChatViewmodelFactory>(p => p.GetRequiredService<ViewmodelFactory>());

                services.AddSingleton<SqliteService>();
                services.AddHostedService<SqliteService>(p => p.GetRequiredService<SqliteService>());
                services.AddSingleton<IPreUiInitializer, SqliteService>(p => p.GetRequiredService<SqliteService>());
                services.AddDbContext<ApplicationDbContext>(options =>
                {
                    options.UseSqlite("Data Source=percolator.db");
                });

                services.AddSingleton<SelfRepository>();
                services.AddSingleton<ISelfProvider>(p => p.GetRequiredService<SelfRepository>());
                services.AddHostedService<SelfRepository>(p => p.GetRequiredService<SelfRepository>());
                services.AddSingleton<IPreUiInitializer, SelfRepository>(p => p.GetRequiredService<SelfRepository>());

                services.AddSingleton<ChatRepository>();

                services.AddSingleton<ISerializer, GrpcSerializer>();
            });

        var host = builder.Build();
        _logger = host.Services.GetRequiredService<ILogger<App>>();

        //todo:remove migrations in production
        SqliteService.EnsureDatabase(host.Services.GetRequiredService<IServiceScopeFactory>(), CancellationToken.None)
            .Wait();

        Task.Factory.StartNew(() => host.StartAsync().Wait());

        var preAppComplete = host.Services.GetServices<IPreUiInitializer>().Select(i => i.PreAppComplete);
        //todo: add a splash screen
        Task.WhenAll(preAppComplete).Wait();

        var mainWindow = host.Services.GetRequiredService<MainWindow>();
        mainWindow.DataContext = host.Services.GetRequiredService<MainWindowViewmodel>();
        mainWindow.Show();
    }

    private void UnhandledExceptionHandler(Exception ex)
    {
        _logger.LogError(ex, "Unhandled exception in app");
    }
}
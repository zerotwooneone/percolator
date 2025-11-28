using System.Configuration;
using System.Data;
using System.Windows;
using Desktop.Wpf.Features.Chat;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Desktop.Wpf.Shared.Navigation;
using Desktop.Wpf.Features.Shell;
using R3;
using Desktop.Wpf.Features.Sessions;

namespace Desktop.Wpf;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    public static IHost? HostInstance { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        HostInstance = Microsoft.Extensions.Hosting.Host
            .CreateDefaultBuilder()
            .ConfigureLogging(builder =>
            {
                builder.ClearProviders();
                builder.AddDebug();
                builder.AddConsole();
            })
            .ConfigureServices(services =>
            {
                // Views
                services.AddSingleton<MainWindow>();
                // Navigation
                services.AddSingleton<INavigationService, NavigationService>();
                // ViewModels
                services.AddSingleton<ShellViewModel>();
                services.AddSingleton<SessionsSidebarViewModel>();
                // Features
                services.AddSingleton<ISessionDirectory, InMemorySessionDirectory>();
                services.AddSingleton<SessionsSidebarView>();
                services.AddSingleton<IChatHistory, InMemoryChatHistory>();
                services.AddSingleton<Desktop.Wpf.Features.Chat.ChatViewModel>();
                services.AddSingleton<Desktop.Wpf.Features.Chat.ChatView>();
            })
            .Build();

        HostInstance.Start();

        var logger = HostInstance.Services.GetRequiredService<ILogger<App>>();
        ObservableSystem.RegisterUnhandledExceptionHandler(ex =>
            logger.LogError(ex, "R3 Unhandled exception"));

        var window = HostInstance.Services.GetRequiredService<MainWindow>();
        window.DataContext = HostInstance.Services.GetRequiredService<ShellViewModel>();
        window.Show();
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        if (HostInstance is not null)
        {
            await HostInstance.StopAsync();
            HostInstance.Dispose();
        }
        base.OnExit(e);
    }
}
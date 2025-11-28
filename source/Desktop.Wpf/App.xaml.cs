using System.Configuration;
using System.Data;
using System.Linq;
using System.Windows;
using System.Windows.Media;
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
                // Per-session scoped chat stack
                services.AddScoped<Desktop.Wpf.Features.Sessions.SessionContext>();
                services.AddScoped<Desktop.Wpf.Features.Chat.ChatViewModel>();
                services.AddScoped<Desktop.Wpf.Features.Chat.ChatView>();
            })
            .Build();

        HostInstance.Start();

        var logger = HostInstance.Services.GetRequiredService<ILogger<App>>();
        ObservableSystem.RegisterUnhandledExceptionHandler(ex =>
            logger.LogError(ex, "R3 Unhandled exception"));

        // Detect Nerd Font family if available and register as a global resource for icons
        try
        {
            var preferred = new[]
            {
                "FiraCode Nerd Font Mono",
                "FiraCode Nerd Font",
                "JetBrainsMono Nerd Font",
                "CaskaydiaCove Nerd Font",
                "Hack Nerd Font",
                "Iosevka Nerd Font"
            };

            FontFamily? chosen = null;
            foreach (var fam in Fonts.SystemFontFamilies)
            {
                // Source: often the family name; FamilyNames has localized names
                var candidates = fam.FamilyNames.Select(kv => kv.Value)
                    .Append(fam.Source)
                    .Where(s => !string.IsNullOrWhiteSpace(s))
                    .Select(s => s!.Trim());

                if (candidates.Any(name => preferred.Any(p => name.Equals(p, System.StringComparison.OrdinalIgnoreCase))))
                {
                    chosen = fam;
                    break;
                }
            }

            // Fallback to Segoe MDL2 Assets (builtin Windows icon font)
            var fallback = new FontFamily("Segoe MDL2 Assets");
            Application.Current.Resources["IconFontFamily"] = chosen ?? fallback;

            logger.LogInformation("Icon font selected: {Font}", (chosen ?? fallback).Source);
        }
        catch (System.Exception ex)
        {
            logger.LogWarning(ex, "Icon font selection failed; falling back to Segoe MDL2 Assets");
            Application.Current.Resources["IconFontFamily"] = new FontFamily("Segoe MDL2 Assets");
        }

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
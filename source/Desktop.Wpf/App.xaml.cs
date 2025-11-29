using System.Linq;
using System.Windows;
using System.Windows.Media;
using Desktop.Wpf.Features.Chat;
using Desktop.Wpf.Shared.Config;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Desktop.Wpf.Shared.Navigation;
using Desktop.Wpf.Features.Shell;
using R3;
using Desktop.Wpf.Features.Sessions;
using Desktop.Wpf.Features.Self;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

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
            .ConfigureLogging((context, builder) =>
            {
                var logging = new LoggingOptions();
                context.Configuration.GetSection("Logging").Bind(logging);

                builder.ClearProviders();
                if (logging.Debug) builder.AddDebug();
                if (logging.Console) builder.AddConsole();
                // Optional: file provider can be added later; leave placeholder
                builder.SetMinimumLevel(logging.Level switch
                {
                    "Trace" => Microsoft.Extensions.Logging.LogLevel.Trace,
                    "Debug" => Microsoft.Extensions.Logging.LogLevel.Debug,
                    "Information" => Microsoft.Extensions.Logging.LogLevel.Information,
                    "Warning" => Microsoft.Extensions.Logging.LogLevel.Warning,
                    "Error" => Microsoft.Extensions.Logging.LogLevel.Error,
                    "Critical" => Microsoft.Extensions.Logging.LogLevel.Critical,
                    "None" => Microsoft.Extensions.Logging.LogLevel.None,
                    _ => Microsoft.Extensions.Logging.LogLevel.Information
                });
            })
            .ConfigureServices((context, services) =>
            {
                // Bind configuration to options
                services.Configure<UiOptions>(context.Configuration.GetSection("Ui"));
                services.Configure<DhtPollingOptions>(context.Configuration.GetSection("Services:DhtPolling"));
                services.Configure<RelayOptions>(context.Configuration.GetSection("Services:Relay"));
                services.Configure<LoggingOptions>(context.Configuration.GetSection("Logging"));

                // Views
                services.AddSingleton<MainWindow>();
                // Navigation
                services.AddSingleton<INavigationService, NavigationService>();
                // ViewModels
                services.AddSingleton<ShellViewModel>();
                services.AddSingleton<SessionsSidebarViewModel>();
                // Features
                services.AddSingleton<Percolator.Cryptography.ISessionRepository, Desktop.Wpf.Features.Sessions.InMemorySessionRepository>();
                services.AddSingleton<SessionsSidebarView>();
                services.AddSingleton<IChatHistory, InMemoryChatHistory>();
                // Per-session scoped chat stack
                services.AddScoped<Desktop.Wpf.Features.Sessions.SessionContext>();
                services.AddScoped<Desktop.Wpf.Features.Chat.ChatViewModel>();
                services.AddScoped<Desktop.Wpf.Features.Chat.ChatView>();

                // Self identity
                services.AddSingleton<SelfIdentity>();
                services.AddSingleton<Percolator.Application.Identity.ISelfIdentityRepository, Desktop.Wpf.Features.Self.InMemorySelfIdentityRepository>();
                // Identity repositories (in-memory fakes for desktop)
                services.AddSingleton<Percolator.Identity.IPeerIdentityRepository, Desktop.Wpf.Features.Identity.InMemoryPeerIdentityRepository>();

                // Startup views
                services.AddSingleton<Desktop.Wpf.Features.Shell.NewUserView>();
            })
            .Build();

        HostInstance.Start();

        var logger = HostInstance.Services.GetRequiredService<ILogger<App>>();
        ObservableSystem.RegisterUnhandledExceptionHandler(ex =>
            logger.LogError(ex, "R3 Unhandled exception"));

        // Self identity loading is orchestrated by ShellViewModel at runtime

        // Detect Nerd Font family if available and register as a global resource for icons
        try
        {
            // Optional configured preferred font name
            var uiOptions = HostInstance.Services.GetRequiredService<IOptionsMonitor<UiOptions>>().CurrentValue;

            var preferred = new[]
            {
                string.IsNullOrWhiteSpace(uiOptions?.NerdFont) ? null : uiOptions!.NerdFont!.Trim(),
                "FiraCode Nerd Font Mono",
                "FiraCode Nerd Font",
                "JetBrainsMono Nerd Font",
                "CaskaydiaCove Nerd Font",
                "Hack Nerd Font",
                "Iosevka Nerd Font"
            }
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Cast<string>()
            .ToArray();

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

            // Fallback to configured or default Segoe MDL2 Assets
            var fallbackName = uiOptions?.IconFontFallback;
            if (string.IsNullOrWhiteSpace(fallbackName)) fallbackName = "Segoe MDL2 Assets";
            var fallback = new FontFamily(fallbackName);
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

    private static string ComputeInitials(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "?";
        var parts = name.Trim().Split(' ', System.StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 1)
            return parts[0].Substring(0, System.Math.Min(2, parts[0].Length)).ToUpperInvariant();
        return (parts[0][0].ToString() + parts[^1][0].ToString()).ToUpperInvariant();
    }
}
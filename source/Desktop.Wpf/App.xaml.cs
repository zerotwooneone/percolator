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

        // Configure icon font: try configured Ui.NerdFont first, then embedded; else log error and fall back to Segoe MDL2 Assets
        try
        {
            var uiOptions = HostInstance.Services.GetRequiredService<IOptionsMonitor<UiOptions>>().CurrentValue;

            FontFamily? selected = null;

            var configured = uiOptions?.NerdFont?.Trim();
            if (!string.IsNullOrWhiteSpace(configured))
            {
                // Try to match an installed system font by family name
                selected = Fonts.SystemFontFamilies.FirstOrDefault(fam =>
                    fam.FamilyNames.Select(kv => kv.Value)
                       .Append(fam.Source)
                       .Any(name => string.Equals(name, configured, System.StringComparison.OrdinalIgnoreCase))
                );

                if (selected is not null)
                {
                    Application.Current.Resources["IconFontFamily"] = selected;
                    logger.LogInformation("Icon font selected (configured): {Font}", selected.Source);
                }
            }

            if (selected is null)
            {
                // Fall back to embedded Nerd Font (pack URI)
                try
                {
                    var embedded = new FontFamily(new Uri("pack://application:,,,/"), "Assets/Fonts/#FiraCode Nerd Font Mono");
                    selected = embedded;
                    Application.Current.Resources["IconFontFamily"] = embedded;
                    logger.LogInformation("Icon font selected (embedded): {Font}", embedded.Source);
                }
                catch (System.Exception ex2)
                {
                    logger.LogError(ex2, "Failed to load embedded icon font");
                }
            }

            if (selected is null)
            {
                logger.LogError("No icon font available (configured nor embedded). Falling back to Segoe MDL2 Assets to avoid crash.");
                Application.Current.Resources["IconFontFamily"] = new FontFamily("Segoe MDL2 Assets");
            }

            // Apply default typography scaling
            var scale = uiOptions?.FontScaling ?? 1.0;
            try
            {
                if (Application.Current.Resources["FontSizeBase"] is double baseSize)
                    Application.Current.Resources["FontSizeBase"] = baseSize * scale;
                if (Application.Current.Resources["FontSizeTitle"] is double titleSize)
                    Application.Current.Resources["FontSizeTitle"] = titleSize * scale;
            }
            catch { /* ignore scaling errors */ }
        }
        catch (System.Exception ex)
        {
            logger.LogError(ex, "Icon font configuration failed unexpectedly");
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
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Input;
using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Desktop.Wpf.Features.Chat;
using Desktop.Wpf.Shared.Config;
using Desktop.Wpf.Shared.Windowing;
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
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Percolator.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Percolator.Application;
using Percolator.Application.Cryptography;
using Percolator.Application.Configuration;
using Percolator.Application.Network;
using Percolator.Cryptography;
using Percolator.Infrastructure.Cryptography;
using Percolator.Infrastructure.Persistence;
using Desktop.Wpf.Features.Simulator;
using MediatR;
using Percolator.MessageQueue.DependencyInjection;

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
            .ConfigureWebHostDefaults(webBuilder =>
            {
                webBuilder.ConfigureKestrel((context, options) =>
                {
                    var port = context.Configuration.GetSection(TransportOptions.SectionName).GetValue<int>("GrpcPort");
                    if (port == 0) port = 5001;
                    options.ListenLocalhost(port, listen =>
                    {
                        listen.Protocols = HttpProtocols.Http2;
                    });
                });

                webBuilder.ConfigureServices(services =>
                {
                    services.AddGrpc(options =>
                    {
                        options.Interceptors.Add<IdentityReadinessInterceptor>();
                    });
                    services.AddSingleton<IdentityReadinessInterceptor>();
                });

                webBuilder.Configure(app =>
                {
                    app.UseRouting();
                    app.UseEndpoints(endpoints =>
                    {
                        endpoints.MapGrpcService<PercolatorMessageService>();
                    });
                });
            })
            .ConfigureServices((context, services) =>
            {
                // Bind configuration to options
                services.Configure<UiOptions>(context.Configuration.GetSection("Ui"));
                services.Configure<DhtPollingOptions>(context.Configuration.GetSection("Services:DhtPolling"));
                services.Configure<RelayOptions>(context.Configuration.GetSection("Services:Relay"));
                services.Configure<LoggingOptions>(context.Configuration.GetSection("Logging"));

                // Core infrastructure (DB, identity, crypto, etc.)
                services.AddInfrastructureServices(context.Configuration);
                services.AddApplicationServices(context.Configuration);
                services.AddScoped<IClock, SystemClock>();
                services.AddMessageQueue();

                // Views
                services.AddSingleton<MainWindow>();
                services.AddScoped<Desktop.Wpf.Features.Simulator.HandshakeSimulatorWindow>();

                // Navigation
                services.AddSingleton<INavigationService, NavigationService>();
                // ViewModels
                services.AddScoped<ShellViewModel>();
                services.AddSingleton<ISessionScopeFactory, SessionScopeFactory>();
                services.AddScoped<SessionsSidebarViewModel>();
                services.AddScoped<Desktop.Wpf.Features.Sessions.PendingHandshakesMenuViewModel>();
                services.AddScoped<Desktop.Wpf.Features.Sessions.SessionShellViewModel>();
                services.AddScoped<Desktop.Wpf.Features.Simulator.HandshakeSimulatorViewModel>();
                services.AddSingleton<Desktop.Wpf.Features.Shell.IIdentityScopeAccessor, Desktop.Wpf.Features.Shell.IdentityScopeAccessor>();
                services.AddSingleton<Desktop.Wpf.Shared.Windowing.IWindowViewRegistry, Desktop.Wpf.Shared.Windowing.WindowViewRegistry>();
                services.AddSingleton<IWindowManager, WindowManager>();
                services.AddMediatR(cfg => cfg.RegisterServicesFromAssembly(typeof(MainWindow).Assembly));

                // Features
                services.AddSingleton<IChatHistory, InMemoryChatHistory>();
                services.AddScoped<IPendingHandshakeSimulatorService, PendingHandshakeSimulatorService>();
                services.AddSingleton<ISimulatorStateStore, JsonSimulatorStateStore>();
                services.AddSingleton<ISimulatedPeerKeyFactory, SimulatedPeerKeyFactory>();
                services.AddSingleton<ISimulatorStateService, SimulatorStateService>();
                services.AddSingleton<ISimulatedPeerDirectory, SimulatedPeerDirectory>();
                services.AddSingleton<Desktop.Wpf.Features.Simulator.ISimulatedPeerPendingInbox, Desktop.Wpf.Features.Simulator.SimulatedPeerPendingInbox>();
                services.AddScoped<Desktop.Wpf.Features.Simulator.ISimulatorRelayEmulator, Desktop.Wpf.Features.Simulator.SimulatorRelayEmulator>();
                services.AddScoped<Desktop.Wpf.Features.Simulator.ISimulatedPeerRuntimeService, Desktop.Wpf.Features.Simulator.SimulatedPeerRuntimeService>();
                services.AddSingleton<Percolator.Application.Network.ISimulatorOutboundInterceptor, Desktop.Wpf.Features.Simulator.SimulatorOutboundInterceptor>();
                // Per-session scoped chat stack
                services.AddScoped<Desktop.Wpf.Features.Sessions.SessionContext>();
                services.AddScoped<Desktop.Wpf.Features.Chat.ChatViewModel>();

                // Self identity
                services.AddSingleton<SelfIdentityModel>();
                services.AddScoped<IStartupIdentityService, StartupIdentityService>();
                // Identity repositories (in-memory fakes for desktop)
                services.AddSingleton<Percolator.Identity.IPeerIdentityRepository, Desktop.Wpf.Features.Identity.InMemoryPeerIdentityRepository>();

                // Startup views
                services.AddSingleton<Desktop.Wpf.Features.Shell.NewUserViewModel>();
            })
            .Build();

        HostInstance.Start();

        // Ensure database schema is created (apply migrations) before any queries run
        using (var scope = HostInstance.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PercolatorDbContext>();
            
            //dev note: change this to db.Database.EnsureCreated(); when using a new db file
            db.Database.Migrate();
        }

        var logger = HostInstance.Services.GetRequiredService<ILogger<App>>();
        ObservableSystem.RegisterUnhandledExceptionHandler(ex =>
            logger.LogError(ex, "R3 Unhandled exception"));

        PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Warning;
        this.DispatcherUnhandledException += (s, exArgs) =>
        {
            try { logger.LogError(exArgs.Exception, "DispatcherUnhandledException"); } catch { }
            //exArgs.Handled = !Debugger.IsAttached;
        };

        AppDomain.CurrentDomain.UnhandledException += (s, exArgs) =>
        {
            var ex = exArgs.ExceptionObject as Exception;
            try { logger.LogCritical(ex, "AppDomain UnhandledException"); } catch { }
        };

        TaskScheduler.UnobservedTaskException += (s, exArgs) =>
        {
            try { logger.LogError(exArgs.Exception, "TaskScheduler UnobservedTaskException"); } catch { }
            exArgs.SetObserved();
        };

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
        _shellScope = HostInstance.Services.CreateScope();

        // Make the shell scope available to window manager and others via identity scope accessor
        var identityScopeAccessor = _shellScope.ServiceProvider.GetRequiredService<Desktop.Wpf.Features.Shell.IIdentityScopeAccessor>();
        identityScopeAccessor.Current = _shellScope.ServiceProvider;

        // Load view mappings from XAML config so WindowManager can resolve VM->Window (fail fast if bad)
        var registry = _shellScope.ServiceProvider.GetRequiredService<Desktop.Wpf.Shared.Windowing.IWindowViewRegistry>();
        Desktop.Wpf.Shared.Windowing.WindowViewMappingLoader.LoadFromResource(
            registry,
            new Uri("/Desktop.Wpf;component/Shared/Windowing/ViewMappings.xaml", UriKind.Relative));

        var shell = _shellScope.ServiceProvider.GetRequiredService<ShellViewModel>();
        window.DataContext = shell;
        window.Show();

    }

    protected override async void OnExit(ExitEventArgs e)
    {
        if (HostInstance is not null)
        {
            _shellScope?.Dispose();
            await HostInstance.StopAsync();
            HostInstance.Dispose();
        }
        base.OnExit(e);
    }

    private IServiceScope? _shellScope;
}
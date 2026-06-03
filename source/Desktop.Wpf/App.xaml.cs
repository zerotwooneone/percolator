using System.Windows;
using System.Windows.Media;
using System.Diagnostics;
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
using Percolator.Infrastructure;
using Percolator.Infrastructure.Chat;
using Microsoft.EntityFrameworkCore;
using Percolator.Application;
using Percolator.Cryptography;
using Percolator.Infrastructure.Persistence;
using Desktop.Wpf.Features.Simulator;
using Desktop.Wpf.Features.Simulator.Protocol;
using Percolator.MessageQueue.DependencyInjection;
using Desktop.Wpf.Features.Sessions.State;
using Desktop.Wpf.Shared.Mvvm;

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

                // Core infrastructure (DB, identity, crypto, etc.)
                services.AddInfrastructureServices(context.Configuration);
                services.AddChatInfrastructure();
                services.AddApplicationServices(context.Configuration);
                services.AddSingleton<IClock, SystemClock>();
                services.AddMessageQueue();
                services.AddScoped<Percolator.Application.Services.IDirectSessionMappingWriter, Percolator.Application.Services.DirectSessionMappingWriter>();

                // Views
                services.AddSingleton<MainWindow>();
                services.AddScoped<Desktop.Wpf.Features.Simulator.HandshakeSimulatorWindow>();
                services.AddScoped<Desktop.Wpf.Features.Sessions.ConnectionManagementDialogWindow>();

                // Navigation
                services.AddSingleton<INavigationService, NavigationService>();
                // ViewModels
                services.AddScoped<ShellViewModel>();
                services.AddSingleton<ISessionScopeFactory, SessionScopeFactory>();
                services.AddScoped<SessionsSidebarViewModel>();
                services.AddScoped<Desktop.Wpf.Features.Sessions.PendingHandshakesMenuViewModel>();
                services.AddScoped<Desktop.Wpf.Features.Sessions.ConnectionManagementDialogViewModel>();
                services.AddScoped<Desktop.Wpf.Features.Sessions.SessionShellViewModel>();
                services.AddScoped<Desktop.Wpf.Features.Simulator.HandshakeSimulatorHostViewModel>();
                services.AddScoped<Desktop.Wpf.Features.Simulator.HandshakeSimulatorViewModel>();
                services.AddScoped<Desktop.Wpf.Features.Simulator.SimulatorPeersTabViewModel>();
                services.AddScoped<Desktop.Wpf.Features.Simulator.SimulatorHandshakesTabViewModel>();
                services.AddScoped<Desktop.Wpf.Features.Simulator.SimulatorRelayTabViewModel>();
                services.AddScoped<Desktop.Wpf.Features.Simulator.SimulatorSessionsTabViewModel>();
                services.AddScoped<Desktop.Wpf.Features.Simulator.SimulatorDiagnosticsTabViewModel>();
                services.AddSingleton<Desktop.Wpf.Features.Shell.IIdentityScopeAccessor, Desktop.Wpf.Features.Shell.IdentityScopeAccessor>();
                services.AddSingleton<Desktop.Wpf.Shared.Windowing.IWindowViewRegistry, Desktop.Wpf.Shared.Windowing.WindowViewRegistry>();
                services.AddSingleton<IWindowManager, WindowManager>();
                services.AddMediatR(cfg => cfg.RegisterServicesFromAssembly(typeof(MainWindow).Assembly));

                // Features
                services.AddSingleton<Desktop.Wpf.Features.Chat.State.ChatStateService>();
                services.AddSingleton<ChatReloadCoordinator>();
                services.AddSingleton<IChatReloadCoordinator>(sp => sp.GetRequiredService<ChatReloadCoordinator>());
                services.AddScoped<Percolator.Application.Sessions.IPeerConnectionSidebarQueries, Percolator.Infrastructure.Sessions.PeerConnectionSidebarQueries>();
                services.AddSingleton<Desktop.Wpf.Features.Sessions.PeerConnectionStateService>();
                services.AddSingleton<Desktop.Wpf.Features.Sessions.PeerConnectionReloadCoordinator>();
                services.AddSingleton<SelectedChannelModel>();
                services.AddSingleton<SelectedPeerConnectionStateCache>();
                services.AddScoped<SelectedChannelPaneViewModel>();
                services.AddScoped<IPendingHandshakeSimulatorService, PendingHandshakeSimulatorService>();
                services.AddSingleton<Desktop.Wpf.Features.Simulator.JsonSimulatorStateRepository>();
                services.AddSingleton<Desktop.Wpf.Features.Simulator.ISimulatorStateRepository>(sp =>
                    new Desktop.Wpf.Features.Simulator.QueuedSimulatorStateRepository(
                        sp.GetRequiredService<Desktop.Wpf.Features.Simulator.JsonSimulatorStateRepository>()));
                services.AddSingleton<ISimulatedPeerKeyFactory, SimulatedPeerKeyFactory>();
                services.AddSingleton<SimulatorStateService>();
                services.AddSingleton<ISimulatorStateService>(sp => sp.GetRequiredService<SimulatorStateService>());
                services.AddSingleton<ISimulatorStateInitializer>(sp => sp.GetRequiredService<SimulatorStateService>());
                services.AddSingleton<IUiDispatcher, WpfUiDispatcher>();
                services.AddSingleton<Desktop.Wpf.Features.Simulator.ISimulatorDelay, Desktop.Wpf.Features.Simulator.SystemSimulatorDelay>();
                services.AddSingleton<Desktop.Wpf.Features.Simulator.ISimulatorDiagnosticsService, Desktop.Wpf.Features.Simulator.SimulatorDiagnosticsService>();
                services.AddSingleton<Desktop.Wpf.Features.Simulator.ISimulatorDiagnosticBundleBuilder, Desktop.Wpf.Features.Simulator.SimulatorDiagnosticBundleBuilder>();
                services.AddSingleton<ISimulatorInitializer, SimulatorInitializer>();
                services.AddSingleton<Desktop.Wpf.Features.Simulator.ISimulatorToMainTransportService, Desktop.Wpf.Features.Simulator.SimulatorToMainTransportService>();
                services.AddScoped<Desktop.Wpf.Features.Simulator.ISimulatorRelayDeliveryService>(sp =>
                    new Desktop.Wpf.Features.Simulator.SimulatorRelayDeliveryService(
                        sp.GetRequiredService<Desktop.Wpf.Features.Simulator.ISimulatorStateService>(),
                        sp.GetRequiredService<Percolator.Identity.ISelfIdentityRepository>(),
                        sp.GetRequiredService<Percolator.Identity.ISelfIdentityKeysStore>(),
                        sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<Desktop.Wpf.Features.Simulator.SimulatorRelayDeliveryService>>(),
                        sp.GetRequiredService<Desktop.Wpf.Features.Simulator.ISimulatorDiagnosticsService>()));
                services.AddSingleton<ISignalProtocolEngine, SignalProtocolEngine>();
                services.AddSingleton<Percolator.Infrastructure.Network.ISimulatorOutboundInterceptor, Desktop.Wpf.Features.Simulator.SimulatorOutboundInterceptor>();
                services.AddScoped<Desktop.Wpf.Features.Simulator.ISimulatorRelayAutoDeliverService, Desktop.Wpf.Features.Simulator.SimulatorRelayAutoDeliverService>();
                // Per-session scoped chat stack
                services.AddScoped<Desktop.Wpf.Features.Sessions.SessionContext>();
                services.AddScoped<Desktop.Wpf.Features.Chat.ChatViewModel>();

                // Self identity
                services.AddSingleton<SelfIdentityModel>();
                services.AddSingleton<Desktop.Wpf.Features.Self.IdentityStateService>();
                services.AddSingleton<Desktop.Wpf.Features.Self.IIdentityStateService>(sp => sp.GetRequiredService<Desktop.Wpf.Features.Self.IdentityStateService>());
                services.AddSingleton<Desktop.Wpf.Features.Self.IIdentityBootstrap>(sp => sp.GetRequiredService<Desktop.Wpf.Features.Self.IdentityStateService>());
                services.AddScoped<IStartupIdentityService, StartupIdentityService>();
                // Identity repositories (in-memory fakes for desktop)
                services.AddScoped<Percolator.Identity.IPeerIdentityRepository, Percolator.Infrastructure.Repositories.SqlitePeerIdentityRepository>();
                services.AddSingleton(TimeProvider.System);

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
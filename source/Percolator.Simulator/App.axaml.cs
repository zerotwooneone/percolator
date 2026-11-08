using System;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Percolator.Simulator.Features.DirectChat;
using Percolator.Simulator.Features.DiscoverySimulation;
using Percolator.Simulator.Features.GroupMessaging;
using Percolator.Simulator.Features.Logging;
using Percolator.Simulator.Features.RichChatAndProfiles;
using Percolator.Simulator.Features.RelayedChat;
using Percolator.Simulator.Features.Scenarios;
using Percolator.Simulator.Features.Shell;
using Percolator.Simulator.Features.Topology;

namespace Percolator.Simulator;

public partial class App : Avalonia.Application
{
    private ServiceProvider? _services;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        var services = new ServiceCollection();

        // 1. Logging Engine (Multi-sink: Channel -> rolling JSONL file + stdout + UI stream)
        var logEngine = new MultiSinkLogEngine();
        services.AddSingleton<ISimulatorLogSink>(logEngine);
        services.AddLogging(builder =>
        {
            builder.SetMinimumLevel(LogLevel.Trace);
            builder.AddProvider(new NodeScopedLoggerProvider("App", SimulatedNodeId.CreateNew(), logEngine));
        });

        // 2. Shared Stateful Services
        services.AddSingleton<ISimulatorStateBus, SimulatorStateBus>();
        services.AddSingleton<ISelectionStateService, SelectionStateService>();
        services.AddSingleton<ISimulatedWorldStateService, SimulatedWorldStateService>();
        services.AddSingleton<IStatusToastService, StatusToastService>();
        services.AddSingleton<IFlightRecorderStateService, FlightRecorderStateService>();
        services.AddSingleton<IVirtualClockStateService, VirtualClockStateService>();
        services.AddSingleton<RichChatActionCoordinator>();
        services.AddSingleton<ProfileSyncCoordinator>();
        services.AddSingleton<ISimulatedNodeFactory, SimulatedNodeFactory>();

        // 3. Shell & Orchestration Services
        services.AddSingleton<DesignDataService>();
        services.AddSingleton<DirectHandshakeOrchestrator>();
        services.AddSingleton<DirectChatCoordinator>();
        services.AddSingleton<AutopilotPongResponder>();
        services.AddSingleton<RelayProvisioningCoordinator>();
        services.AddSingleton<GroupChatCoordinator>();
        services.AddSingleton<DiscoverySimulationCoordinator>();
        services.AddSingleton<ScenarioRunnerService>();
        services.AddSingleton<PersonaCardDependencies>();

        // 4. ViewModels
        services.AddTransient<TopGlobalToolbarViewModel>();
        services.AddTransient<ContextualActionBarViewModel>();
        services.AddTransient<RosterViewModel>();
        services.AddTransient<WorkbenchViewModel>();
        services.AddTransient<FlightRecorderViewModel>();
        services.AddTransient<LogDockViewModel>();
        services.AddTransient<MainWindowViewModel>();
        services.AddTransient<GroupPanelViewModel>();

        _services = services.BuildServiceProvider();

        // Eagerly resolve autopilot pong responder so it activates its state bus listener
        _services.GetRequiredService<AutopilotPongResponder>();

        // Log initialization event
        logEngine.Log(new SimulatorLogEntry(
            System.DateTimeOffset.UtcNow,
            LogLevel.Information,
            "Simulator",
            null,
            "Lifecycle",
            "Percolator Simulator MultiSinkLogEngine initialized. Logging active."));

#if !WINDOWS
        if (OperatingSystem.IsWindows())
        {
            var warning = "TARGET MISMATCH: Running on Windows with generic 'net10.0' target. WindowsSChannelCertificateProvider and WindowsSparseDesktopFileStore are NOT compiled in. Use target 'net10.0-windows10.0.19041.0' instead.";
            logEngine.Log(new SimulatorLogEntry(
                System.DateTimeOffset.UtcNow,
                LogLevel.Warning,
                "Simulator",
                null,
                "PlatformWarning",
                warning));
        }
#endif

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // Normal UI startup
            var mainWindow = new MainWindow
            {
                DataContext = _services.GetRequiredService<MainWindowViewModel>()
            };

            desktop.MainWindow = mainWindow;
        }

        base.OnFrameworkInitializationCompleted();
    }
}

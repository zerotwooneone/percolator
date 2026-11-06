using System;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Percolator.Simulator.Features.DirectChat;
using Percolator.Simulator.Features.Logging;
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
        services.AddSingleton<ISimulatedNodeFactory, SimulatedNodeFactory>();

        // 3. Shell & Orchestration Services
        services.AddSingleton<DesignDataService>();
        services.AddSingleton<DirectHandshakeOrchestrator>();
        services.AddSingleton<DirectChatCoordinator>();
        services.AddSingleton<AutopilotPongResponder>();
        services.AddSingleton<RelayProvisioningCoordinator>();
        services.AddSingleton<ScenarioRunnerService>();

        // 4. ViewModels
        services.AddTransient<TopGlobalToolbarViewModel>();
        services.AddTransient<ContextualActionBarViewModel>();
        services.AddTransient<RosterViewModel>();
        services.AddTransient<WorkbenchViewModel>();
        services.AddTransient<FlightRecorderViewModel>();
        services.AddTransient<LogDockViewModel>();
        services.AddTransient<MainWindowViewModel>();

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
            logEngine.Log(new SimulatorLogEntry(
                System.DateTimeOffset.UtcNow,
                LogLevel.Error,
                "Simulator",
                null,
                "PlatformGuard",
                "PLATFORM TARGET MISMATCH: Running on Windows with generic 'net10.0' target. WindowsSChannelCertificateProvider and WindowsSparseDesktopFileStore are NOT compiled in. Use target 'net10.0-windows10.0.19041.0' instead."));
        }
#endif

        // Seed preview design data in Phase 1
        var designData = _services.GetRequiredService<DesignDataService>();
        designData.SeedPreviewData();

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new MainWindow
            {
                DataContext = _services.GetRequiredService<MainWindowViewModel>()
            };

            desktop.Exit += async (_, _) =>
            {
                var pong = _services?.GetService<AutopilotPongResponder>();
                pong?.Dispose();

                var world = _services?.GetService<ISimulatedWorldStateService>();
                if (world != null)
                {
                    await world.ClearAllAsync();
                }

                logEngine.Dispose();
            };
        }

        base.OnFrameworkInitializationCompleted();
    }
}

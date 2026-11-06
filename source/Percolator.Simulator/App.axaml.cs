using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Microsoft.Extensions.DependencyInjection;
using Percolator.Simulator.Features.Logging;
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

        // 1. Shared Stateful Services
        services.AddSingleton<ISimulatorStateBus, SimulatorStateBus>();
        services.AddSingleton<ISelectionStateService, SelectionStateService>();
        services.AddSingleton<ISimulatedWorldStateService, SimulatedWorldStateService>();
        services.AddSingleton<IStatusToastService, StatusToastService>();

        // 2. Shell & Domain Services
        services.AddSingleton<DesignDataService>();

        // 3. ViewModels
        services.AddTransient<TopGlobalToolbarViewModel>();
        services.AddTransient<ContextualActionBarViewModel>();
        services.AddTransient<RosterViewModel>();
        services.AddTransient<WorkbenchViewModel>();
        services.AddTransient<FlightRecorderViewModel>();
        services.AddTransient<LogDockViewModel>();
        services.AddTransient<MainWindowViewModel>();

        _services = services.BuildServiceProvider();

        // Seed preview design data in Phase 1
        var designData = _services.GetRequiredService<DesignDataService>();
        designData.SeedPreviewData();

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new MainWindow
            {
                DataContext = _services.GetRequiredService<MainWindowViewModel>()
            };
        }

        base.OnFrameworkInitializationCompleted();
    }
}

using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Percolator.Application2.Ports;
using Percolator.Desktop.Common.Services;
using Percolator.Desktop.Common.Windowing;
using Percolator.Desktop.Features.IdentityGate.ViewModels;
using Percolator.Desktop.Features.IdentityGate.Views;
using Percolator.Desktop.ViewModels;
using Percolator.Domain.Common;
using Percolator.Domain.Identities.Ports;
using R3;

namespace Percolator.Desktop;

public partial class App : Application
{
    private IHost? _host;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);

        // R3 Global unhandled exception handler to prevent silent reactive crashes
        ObservableSystem.RegisterUnhandledExceptionHandler(ex =>
        {
            Console.Error.WriteLine($"[R3 Unhandled Exception] {ex}");
        });

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            Console.Error.WriteLine($"[AppDomain Unhandled Exception] {e.ExceptionObject}");
        };

        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Console.Error.WriteLine($"[TaskScheduler Unobserved Exception] {e.Exception}");
            e.SetObserved();
        };
    }

    public override void OnFrameworkInitializationCompleted()
    {
        // Build and start Microsoft.Extensions.Hosting host
        _host = Host.CreateDefaultBuilder()
            .ConfigureServices((_, services) =>
            {
                // Core Infrastructure & Utilities
                services.AddSingleton<IDateTimeProvider>(SystemDateTimeProvider.Instance);

                // Privacy & Network Stealth Subsystems
                services.AddSingleton<INetworkStealthGate, NetworkStealthGate>();

                // Identity & Profile Repositories
                services.AddSingleton<IDesktopIdentityKeyStore, DesktopLocalIdentityKeyStore>();
                services.AddSingleton<ILocalIdentityKeyStore>(sp => sp.GetRequiredService<IDesktopIdentityKeyStore>());
                services.AddSingleton<IIdentityProfileRepository, DesktopIdentityProfileRepository>();

                // Window Lifecycle & Scoping
                services.AddSingleton<IWindowManager, WindowManager>();

                // ViewModels
                services.AddTransient<IdentityGateViewModel>();
                services.AddTransient<MainWindowViewModel>();
            })
            .Build();

        _host.Start();

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // Initial Window is strictly the IdentityGateWindow to enforce total network stealth on launch
            var identityGateVm = _host.Services.GetRequiredService<IdentityGateViewModel>();
            desktop.MainWindow = new IdentityGateWindow
            {
                DataContext = identityGateVm
            };

            desktop.Exit += async (_, _) =>
            {
                if (_host != null)
                {
                    await _host.StopAsync();
                    _host.Dispose();
                }
            };
        }

        base.OnFrameworkInitializationCompleted();
    }
}

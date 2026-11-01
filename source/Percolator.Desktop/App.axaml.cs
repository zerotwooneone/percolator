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

public partial class App : Avalonia.Application
{
    private IHost? _host;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);

        // R3 Global unhandled exception handler to prevent silent reactive crashes
        ObservableSystem.RegisterUnhandledExceptionHandler(ex =>
        {
            Console.Error.WriteLine($"[FATAL UNHANDLED REACTIVE EXCEPTION]: {ex}");
        });
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // Set AppShutdownMode.OnExplicitShutdown so the process doesn't terminate
            // when the Gate window closes during the transition to the Main window.
            desktop.ShutdownMode = Avalonia.Controls.ShutdownMode.OnExplicitShutdown;

            // Instantiate IdentityGate Window and ViewModel
            var gateViewModel = new IdentityGateViewModel(
                onUnlocked: OnVaultUnlocked,
                onCreated: OnVaultCreated);

            var gateWindow = new IdentityGateWindow
            {
                DataContext = gateViewModel
            };

            desktop.MainWindow = gateWindow;
            gateWindow.Show();
        }

        base.OnFrameworkInitializationCompleted();
    }

    private async Task OnVaultUnlocked(AuthenticatedSession session)
    {
        var desktop = (IClassicDesktopStyleApplicationLifetime)ApplicationLifetime!;
        var previousWindow = desktop.MainWindow;

        // Build Stage 2 Host using the unlocked session credentials
        _host = BuildStage2Host(session);
        await _host.StartAsync();

        // Instantiate MainWindow with MainViewModel from Host DI
        var mainWindow = new MainWindow
        {
            DataContext = _host.Services.GetRequiredService<MainViewModel>()
        };

        desktop.MainWindow = mainWindow;
        mainWindow.Show();

        // Close the unlock gate
        previousWindow?.Close();

        // Restore normal shutdown behavior when the main window closes
        desktop.ShutdownMode = Avalonia.Controls.ShutdownMode.OnMainWindowClose;
    }

    private async Task OnVaultCreated(AuthenticatedSession session)
    {
        // Vault creation flows straight into the unlocked state
        await OnVaultUnlocked(session);
    }

    private static IHost BuildStage2Host(AuthenticatedSession session)
    {
        return Host.CreateDefaultBuilder()
            .ConfigureServices((context, services) =>
            {
                // Register session and Stage 2 authenticated services
                services.AddSingleton(session);
                services.AddSingleton<MainViewModel>();
                // Stage 2 services will be registered here as infrastructure expands
            })
            .Build();
    }
}

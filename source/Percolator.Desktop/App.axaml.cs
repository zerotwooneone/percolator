// SPECIFICATION GOVERNANCE: Read Percolator.Desktop/spec/CodeBehind.spec.md before modifying this file.

using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Percolator.Desktop.Common.Services;
using Percolator.Desktop.Common.Windowing;
using Percolator.Desktop.Features.Shell.ViewModels;
using Percolator.Desktop.Features.Startup.ViewModels;
using Percolator.Desktop.Features.Startup.Views;
using Percolator.Domain.Common;
using Percolator.Infrastructure.DependencyInjection;
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
        // Stage 1 bootstrap container: vault unlock infrastructure + presentation coordinators only.
        // Zero DbContexts, repositories, or network listeners exist pre-unlock
        // ([DEP-DESKTOP-TWO-STAGE-CONTAINER-LIFECYCLE], [DEP-DESKTOP-PRE-ACTIVATION-STEALTH]).
        _host = Host.CreateDefaultBuilder()
            .ConfigureServices((_, services) =>
            {
                // Vault unlock stack: header storage, Argon2id envelope packer, canary probe,
                // duress wipe handler, IDatabaseUnlockCoordinator (AddBootstrapInfrastructure
                // also initializes SQLitePCL.Batteries_V2).
                services.AddBootstrapInfrastructure();

                services.AddSingleton<IDateTimeProvider>(SystemDateTimeProvider.Instance);
                services.AddSingleton<INativeCryptoProbe, NativeCryptoProbe>();
                services.AddSingleton<IDialogService, DialogService>();
                services.AddSingleton<ILauncherService, LauncherService>();
                services.AddSingleton<IWindowManager, WindowManager>();
                services.AddSingleton<IWindowHandoffCoordinator, WindowHandoffCoordinator>();

                services.AddTransient<StartupHostViewModel>();
                services.AddTransient<VaultSetupViewModel>();
                services.AddTransient<VaultUnlockViewModel>();
                services.AddTransient<IdentityGateViewModel>();
                services.AddTransient<MainWindowViewModel>();
            })
            .Build();

        _host.Start();

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // DESK-02: boot into the unified 480×600 startup frame (vault gate).
            // The Stage 2 authenticated host and MainWindow launch only after unlock +
            // persona confirmation ([INV-DESKTOP-TWO-STAGE-HANDOFF]).
            desktop.MainWindow = new StartupWindow(_host.Services.GetRequiredService<StartupHostViewModel>());

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

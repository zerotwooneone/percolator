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
using Percolator.Desktop.Features.Shell.Views;
using Percolator.Domain.Common;
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
        // Stage 1 bootstrap container: presentation coordinators only.
        // No DbContexts, repositories, or network listeners may exist pre-unlock
        // ([DEP-DESKTOP-TWO-STAGE-CONTAINER-LIFECYCLE], [DEP-DESKTOP-PRE-ACTIVATION-STEALTH]).
        _host = Host.CreateDefaultBuilder()
            .ConfigureServices((_, services) =>
            {
                services.AddSingleton<IDateTimeProvider>(SystemDateTimeProvider.Instance);
                services.AddSingleton<IWindowManager, WindowManager>();

                services.AddTransient<MainWindowViewModel>();
            })
            .Build();

        _host.Start();

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // DESK-01: boot directly into the responsive shell frame.
            // DESK-02..04 will front this with the StartupWindow vault/persona gates.
            desktop.MainWindow = new MainWindow(_host.Services.GetRequiredService<MainWindowViewModel>());

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

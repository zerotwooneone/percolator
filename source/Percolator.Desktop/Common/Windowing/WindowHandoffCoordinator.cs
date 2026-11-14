// SPECIFICATION GOVERNANCE: Read Percolator.Desktop/spec/CodeBehind.spec.md before modifying this file.

using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Percolator.Application.Ports;
using Percolator.Desktop.Features.Shell.ViewModels;
using Percolator.Desktop.Features.Shell.Views;
using Percolator.Desktop.Features.Startup.Models;
using Percolator.Desktop.Features.Startup.ViewModels;
using Percolator.Desktop.Features.Startup.Views;

namespace Percolator.Desktop.Common.Windowing;

/// <summary>
/// Default <see cref="IWindowHandoffCoordinator"/> realizing [INV-DESKTOP-TWO-STAGE-HANDOFF].
/// All window mutations marshal onto the UI thread ([CB-009], [R3-009]).
/// </summary>
public sealed class WindowHandoffCoordinator : IWindowHandoffCoordinator
{
    private readonly IServiceProvider _services;
    private readonly IDatabaseUnlockCoordinator _unlockCoordinator;
    private readonly ILogger<WindowHandoffCoordinator> _logger;

    // Port leases acquired at the Identity Gate; held for the persona session lifetime and
    // released on LockSessionAsync ([INV-DESKTOP-STEALTH-ACTIVATION-GATE]).
    private readonly List<PersonaLaunchDescriptor> _activePersonas = new();
    private readonly List<ExchangeLaunchDescriptor> _activeExchanges = new();

    public WindowHandoffCoordinator(
        IServiceProvider services,
        IDatabaseUnlockCoordinator unlockCoordinator,
        ILogger<WindowHandoffCoordinator> logger)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _unlockCoordinator = unlockCoordinator ?? throw new ArgumentNullException(nameof(unlockCoordinator));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    private static IClassicDesktopStyleApplicationLifetime Desktop =>
        Avalonia.Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime
        ?? throw new InvalidOperationException("Desktop application lifetime is not available.");

    public async Task TransitionToGateAsync(IAuthenticatedSession? session = null, CancellationToken ct = default)
    {
        await Dispatcher.UIThread.InvokeAsync(async () =>
        {
            if (Desktop.MainWindow is StartupWindow { DataContext: StartupHostViewModel host })
            {
                await host.NavigateToGateAsync(session);
            }
            else
            {
                _logger.LogWarning(
                    "TransitionToGateAsync invoked without an open StartupWindow host; navigation skipped.");
            }
        });
    }

    public async Task TransitionToMainShellAsync(
        IAuthenticatedSession? session,
        IReadOnlyList<PersonaLaunchDescriptor>? personas = null,
        IReadOnlyList<ExchangeLaunchDescriptor>? exchanges = null,
        CancellationToken ct = default)
    {
        // Launches all registered IHostedService background workers in the Stage 2 container.
        // The duress decoy path passes a null session — no services start.
        if (session is not null)
        {
            await session.StartAsync(ct).ConfigureAwait(false);
        }

        lock (_activePersonas)
        {
            _activePersonas.Clear();
            if (personas is not null)
            {
                _activePersonas.AddRange(personas);
            }

            _activeExchanges.Clear();
            if (exchanges is not null)
            {
                _activeExchanges.AddRange(exchanges);
            }
        }

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var desktop = Desktop;
            var previous = desktop.MainWindow;

            var mainWindow = new MainWindow(_services.GetRequiredService<MainWindowViewModel>());
            desktop.MainWindow = mainWindow;
            mainWindow.Show();

            previous?.Close();
        });
    }

    public async Task LockSessionAsync(CancellationToken ct = default)
    {
        // Flushes pools, zeroizes the key buffer, disposes the authenticated host.
        await _unlockCoordinator.LockAsync().ConfigureAwait(false);

        lock (_activePersonas)
        {
            foreach (var persona in _activePersonas)
            {
                persona.Lease.Dispose();
            }

            foreach (var exchange in _activeExchanges)
            {
                exchange.Lease.Dispose();
            }

            _activePersonas.Clear();
            _activeExchanges.Clear();
        }

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var desktop = Desktop;
            var previous = desktop.MainWindow;

            // Fresh transient host VM guarantees no passphrase/state residue after lock.
            var hostViewModel = _services.GetRequiredService<StartupHostViewModel>();
            var startupWindow = new StartupWindow(hostViewModel);
            desktop.MainWindow = startupWindow;
            startupWindow.Show();

            if (previous is not null && !ReferenceEquals(previous, startupWindow))
            {
                previous.Close();
            }
        });
    }
}

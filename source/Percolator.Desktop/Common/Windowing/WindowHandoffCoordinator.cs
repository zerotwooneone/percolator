// SPECIFICATION GOVERNANCE: Read Percolator.Desktop/spec/CodeBehind.spec.md before modifying this file.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Percolator.Application.Ports;
using Percolator.Application.Session.Ports;
using Percolator.Desktop.Common.Services;
using Percolator.Desktop.Features.Shell.Models;
using Percolator.Desktop.Features.Shell.ViewModels;
using Percolator.Desktop.Features.Shell.Views;
using Percolator.Desktop.Features.Startup.Models;
using Percolator.Desktop.Features.Startup.ViewModels;
using Percolator.Desktop.Features.Startup.Views;
using Percolator.Domain.Identities;

namespace Percolator.Desktop.Common.Windowing;

/// <summary>
/// Default <see cref="IWindowHandoffCoordinator"/> realizing [INV-DESKTOP-TWO-STAGE-HANDOFF].
/// All window mutations marshal onto the UI thread ([CB-009], [R3-009]).
/// </summary>
public sealed class WindowHandoffCoordinator : IWindowHandoffCoordinator
{
    private readonly IDatabaseUnlockCoordinator _unlockCoordinator;
    private readonly Func<StartupHostViewModel> _startupHostFactory;
    private readonly ILogger<WindowHandoffCoordinator> _logger;

    // Port leases acquired at the Identity Gate; held for the persona session lifetime and
    // released on LockSessionAsync ([INV-DESKTOP-STEALTH-ACTIVATION-GATE]).
    private readonly List<PersonaLaunchDescriptor> _activePersonas = new();
    private readonly List<ExchangeLaunchDescriptor> _activeExchanges = new();

    // Armed when the shell is constructed so LockSessionAsync can await the window's
    // Closed event — the Closing geometry save must land before session teardown (WIN-02).
    private TaskCompletionSource _shellClosed =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public WindowHandoffCoordinator(
        IDatabaseUnlockCoordinator unlockCoordinator,
        Func<StartupHostViewModel> startupHostFactory,
        ILogger<WindowHandoffCoordinator> logger)
    {
        _unlockCoordinator = unlockCoordinator ?? throw new ArgumentNullException(nameof(unlockCoordinator));
        _startupHostFactory = startupHostFactory ?? throw new ArgumentNullException(nameof(startupHostFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task TransitionToGateAsync(IAuthenticatedSession session, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(session);

        if (Avalonia.Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            await Dispatcher.UIThread.InvokeAsync(async () =>
            {
                if (desktop.MainWindow is StartupWindow { DataContext: StartupHostViewModel host })
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
        else
        {
            _logger.LogWarning(
                "TransitionToGateAsync invoked without an available desktop application lifetime; navigation skipped.");
        }
    }

    public async Task TransitionToMainShellAsync(
        IAuthenticatedSession session,
        IReadOnlyList<PersonaLaunchDescriptor> personas,
        IReadOnlyList<ExchangeLaunchDescriptor> exchanges,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(personas);
        ArgumentNullException.ThrowIfNull(exchanges);

        // 1. Seed active personas in IActiveIdentitySet BEFORE session.StartAsync so
        // hosted workers (DrainScheduler / RelayMailboxDrainHost) observe a populated set.
        var activeIdentities = session.Services.GetRequiredService<IActiveIdentitySet>();
        foreach (var persona in personas)
        {
            await activeIdentities.AddAsync(persona.IdentityId, ct).ConfigureAwait(false);
        }

        // Seed active personas presentation models in ICurrentPersonaService
        if (session.Services.GetService<ICurrentPersonaService>() is { } personaService && personas.Count > 0)
        {
            personaService.InitializeActivePersonas(
                personas.Select(p => new ActivePersonaModel(
                    p.IdentityId,
                    p.DisplayName,
                    p.Lease.Port)));
        }

        // 2. Launches all registered IHostedService background workers in the Stage 2 container.
        await session.StartAsync(ct).ConfigureAwait(false);

        lock (_activePersonas)
        {
            _activePersonas.Clear();
            _activePersonas.AddRange(personas);

            _activeExchanges.Clear();
            _activeExchanges.AddRange(exchanges);
        }

        if (Avalonia.Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            await Dispatcher.UIThread.InvokeAsync(async () =>
            {
                var previous = desktop.MainWindow;

                // Composition seam: the shell VM + geometry store live in the session container.
                var geometryStore = session.Services.GetRequiredService<IShellWindowGeometryStore>();
                var mainWindow = new MainWindow(
                    session.Services.GetRequiredService<MainWindowViewModel>(),
                    geometryStore);

                // Restore before Show — absent/corrupt record keeps the XAML CenterScreen default.
                var geometry = await geometryStore.LoadAsync(ct).ConfigureAwait(true);
                mainWindow.ApplyGeometry(geometry);

                _shellClosed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                mainWindow.Closed += (_, _) => _shellClosed.TrySetResult();

                desktop.MainWindow = mainWindow;
                mainWindow.Show();

                previous?.Close();
            });
        }
        else
        {
            _logger.LogWarning(
                "TransitionToMainShellAsync invoked without an available desktop application lifetime; UI window display skipped.");
        }
    }

    public async Task LockSessionAsync(CancellationToken ct = default)
    {
        // WIN-02 close ordering: the geometry store dies with the session container, so the
        // shell's Closing save must land BEFORE LockAsync tears the session down. Show the
        // startup frame first (keeps the app alive under OnLastWindowClose), close the
        // shell, and await its Closed event — the async-close save completes while the
        // session is still live.
        Task? shellClosed = null;
        if (Avalonia.Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                var previous = desktop.MainWindow;

                // Fresh transient host VM guarantees no passphrase/state residue after lock.
                var hostViewModel = _startupHostFactory();
                var startupWindow = new StartupWindow(hostViewModel);
                desktop.MainWindow = startupWindow;
                startupWindow.Show();

                if (previous is not null && !ReferenceEquals(previous, startupWindow))
                {
                    previous.Close();
                    if (previous is MainWindow)
                    {
                        shellClosed = _shellClosed.Task;
                    }
                }
            });
        }
        else
        {
            _logger.LogWarning(
                "LockSessionAsync invoked without an available desktop application lifetime; UI window navigation skipped.");
        }

        if (shellClosed is not null)
        {
            await shellClosed.ConfigureAwait(false);
        }

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
    }
}

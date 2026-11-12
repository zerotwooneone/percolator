// SPECIFICATION GOVERNANCE: Read Percolator.Desktop/spec/ViewModel.spec.md before modifying this file.

using System;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Percolator.Application2.Ports;
using Percolator.Desktop.Common.Services;
using Percolator.Desktop.Features.Startup.Models;
using Percolator.Desktop.ViewModels;
using Percolator.Domain.Security.ValueObjects;
using Percolator.Infrastructure.Vault;

namespace Percolator.Desktop.Features.Startup.ViewModels;

/// <summary>
/// Host ViewModel for the unified 480×600 StartupWindow frame. Determines the vault state
/// on boot and projects the active screen (Setup / Unlock / Identity Gate) for the
/// view's dynamic transition ([DEP-DESKTOP-STARTUP-VAULT-AUTHENTICATION]).
/// </summary>
public partial class StartupHostViewModel : ViewModelBase
{
    private const int ExpectedCryptoSmokeResult = 42;

    private readonly IDatabaseUnlockCoordinator _unlockCoordinator;
    private readonly INativeCryptoProbe _cryptoProbe;
    private readonly VaultStorageOptions _storageOptions;
    private readonly IServiceProvider _services;
    private readonly IHostApplicationLifetime _appLifetime;
    private readonly IDialogService _dialogs;
    private readonly ILauncherService _launcher;
    private readonly ILogger<StartupHostViewModel> _logger;

    [ObservableProperty]
    private StartupScreen _currentScreen = StartupScreen.None;

    [ObservableProperty]
    private ViewModelBase? _currentViewModel;

    [ObservableProperty]
    private bool _isBusy;

    /// <summary>The vault data directory, surfaced for the error-dialog "Open Data Folder" action.</summary>
    public string DataDirectoryPath => _storageOptions.BaseDirectory;

    /// <summary>
    /// The authenticated Stage 2 session parked between vault unlock and persona launch
    /// (the Identity Gate hands it to the shell transition).
    /// </summary>
    public IAuthenticatedSession? PendingSession { get; private set; }

    public StartupHostViewModel(
        IDatabaseUnlockCoordinator unlockCoordinator,
        INativeCryptoProbe cryptoProbe,
        VaultStorageOptions storageOptions,
        IServiceProvider services,
        IHostApplicationLifetime appLifetime,
        IDialogService dialogs,
        ILauncherService launcher,
        ILogger<StartupHostViewModel> logger)
    {
        _unlockCoordinator = unlockCoordinator ?? throw new ArgumentNullException(nameof(unlockCoordinator));
        _cryptoProbe = cryptoProbe ?? throw new ArgumentNullException(nameof(cryptoProbe));
        _storageOptions = storageOptions ?? throw new ArgumentNullException(nameof(storageOptions));
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _appLifetime = appLifetime ?? throw new ArgumentNullException(nameof(appLifetime));
        _dialogs = dialogs ?? throw new ArgumentNullException(nameof(dialogs));
        _launcher = launcher ?? throw new ArgumentNullException(nameof(launcher));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>Immutable projection of current startup state ([INV-DESKTOP-PRESENTATION-MODELS]).</summary>
    public StartupStateModel ToSnapshot() => new(CurrentScreen, IsBusy, null);

    /// <summary>
    /// Startup sequence: native crypto smoke test, vault status determination,
    /// then screen selection. Invoked once from StartupWindow's Loaded handler ([CB-003]).
    /// </summary>
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        IsBusy = true;
        try
        {
            // 1. Security Engine smoke test — failure is terminal.
            if (_cryptoProbe.TestConnection() != ExpectedCryptoSmokeResult)
            {
                await HandleStartupErrorAsync(StartupErrorKind.SecurityEngineError);
                return;
            }

            // 2. Vault status — Damaged means the header exists but cannot be parsed.
            DatabaseSetupStatus status;
            try
            {
                status = await _unlockCoordinator.DetermineStatusAsync(ct).ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Vault status determination failed.");
                await HandleStartupErrorAsync(StartupErrorKind.VaultDataDamaged);
                return;
            }

            if (status == DatabaseSetupStatus.Damaged)
            {
                _logger.LogError("Vault header exists but could not be parsed.");
                await HandleStartupErrorAsync(StartupErrorKind.VaultDataDamaged);
                return;
            }

            NavigateToScreen(status switch
            {
                DatabaseSetupStatus.Uninitialized => StartupScreen.VaultSetup,
                DatabaseSetupStatus.Locked => StartupScreen.VaultUnlock,
                // Already unlocked (e.g. session survived a soft lock): go straight to the gate.
                _ => StartupScreen.IdentityGate
            });
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// Moves the frame forward to Screen 2 (Identity Gate). Called by
    /// <c>WindowHandoffCoordinator</c> after unlock, initialize, or duress activation.
    /// </summary>
    public void NavigateToGate(IAuthenticatedSession? session = null)
    {
        PendingSession = session;
        NavigateToScreen(StartupScreen.IdentityGate);
    }

    /// <summary>Returns the frame to the unlock screen (session relock path).</summary>
    public void NavigateToUnlock()
    {
        PendingSession = null;
        NavigateToScreen(StartupScreen.VaultUnlock);
    }

    private void NavigateToScreen(StartupScreen screen)
    {
        CurrentViewModel = screen switch
        {
            StartupScreen.VaultSetup => _services.GetRequiredService<VaultSetupViewModel>(),
            StartupScreen.VaultUnlock => _services.GetRequiredService<VaultUnlockViewModel>(),
            StartupScreen.IdentityGate => _services.GetRequiredService<IdentityGateViewModel>(),
            _ => null
        };
        CurrentScreen = screen;
    }

    private async Task HandleStartupErrorAsync(StartupErrorKind kind)
    {
        var (title, message, primary) = kind switch
        {
            StartupErrorKind.SecurityEngineError => (
                "Security Engine Error",
                "The native cryptographic library (signal_shim) could not be loaded on your system.",
                "View Diagnostic Log"),
            _ => (
                "Vault Data Damaged",
                "The database file could not be opened. It may have been damaged or altered.",
                "Open Data Folder")
        };

        var result = await _dialogs
            .ShowAsync(new DialogRequest(title, message, primary, "Exit"))
            .ConfigureAwait(true);

        if (result == DialogResult.Primary)
        {
            // "Open Data Folder" / "View Diagnostic Log" — the vault directory holds both.
            await _launcher.OpenDirectoryAsync(_storageOptions.BaseDirectory).ConfigureAwait(true);
        }

        _appLifetime.StopApplication();
    }
}

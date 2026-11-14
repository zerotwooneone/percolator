// SPECIFICATION GOVERNANCE: Read Percolator.Desktop/spec/ViewModel.spec.md before modifying this file.

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Percolator.Application.Ports;
using Percolator.Desktop.Common.Services;
using Percolator.Desktop.Common.Windowing;
using Percolator.Desktop.Features.Startup.Models;
using Percolator.Desktop.ViewModels;
using Percolator.Domain.Security.ValueObjects;
using IPortReservationService = Percolator.Desktop.Common.Services.IPortReservationService;

namespace Percolator.Desktop.Features.Startup.ViewModels;

/// <summary>
/// ViewModel for State 1B (returning-user unlock). Evaluates the candidate passphrase
/// via <see cref="IDatabaseUnlockCoordinator.UnlockAsync"/> and routes the result:
/// master → gate handoff, duress → silent decoy transition, invalid → inline error.
/// </summary>
public partial class VaultUnlockViewModel : ViewModelBase
{
    private readonly IDatabaseUnlockCoordinator _unlockCoordinator;
    private readonly IWindowHandoffCoordinator _handoff;
    private readonly IPersonaDirectory _personas;
    private readonly IPortReservationService _ports;

    [ObservableProperty]
    private string _password = string.Empty;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string? _errorMessage;

    /// <summary>
    /// Monotonic counter incremented on each failed unlock attempt. The view increments
    /// a subtle shake animation off this notification ([CB-001] animation stays in view).
    /// </summary>
    [ObservableProperty]
    private int _failedAttemptNonce;

    public VaultUnlockViewModel(
        IDatabaseUnlockCoordinator unlockCoordinator,
        IWindowHandoffCoordinator handoff,
        IPersonaDirectory personas,
        IPortReservationService ports)
    {
        _unlockCoordinator = unlockCoordinator ?? throw new ArgumentNullException(nameof(unlockCoordinator));
        _handoff = handoff ?? throw new ArgumentNullException(nameof(handoff));
        _personas = personas ?? throw new ArgumentNullException(nameof(personas));
        _ports = ports ?? throw new ArgumentNullException(nameof(ports));
    }

    [RelayCommand]
    private Task UnlockAsync(CancellationToken ct) => UnlockCoreAsync(ct, forceGate: false);

    /// <summary>
    /// Shift-override path ([INV-DESKTOP-STEALTH-ACTIVATION-GATE]): bypasses the persona
    /// auto-start preference and always lands on the Identity Gate. Bound declaratively
    /// via Shift+Enter in the view ([VIEW-009]).
    /// </summary>
    [RelayCommand]
    private Task ForceGateUnlockAsync(CancellationToken ct) => UnlockCoreAsync(ct, forceGate: true);

    private async Task UnlockCoreAsync(CancellationToken ct, bool forceGate)
    {
        if (IsBusy)
        {
            return;
        }

        ErrorMessage = null;
        IsBusy = true;

        var candidate = VaultPassphrase.Create(Password);
        try
        {
            var result = await _unlockCoordinator.UnlockAsync(candidate, ct).ConfigureAwait(true);
            switch (result)
            {
                case UnlockResult.Success success when !forceGate:
                    await TryAutoStartLaunchAsync(success.Session, ct).ConfigureAwait(true);
                    break;

                case UnlockResult.Success success:
                    await _handoff.TransitionToGateAsync(success.Session, ct).ConfigureAwait(true);
                    break;

                case UnlockResult.DuressActivated duress:
                    // Silent decoy transition: zero error surfacing. The decoy session (when the
                    // post-wipe decoy vault was created) lets personas persist into it — a working
                    // vault is the deniable outcome; a null session falls back to in-memory decoy.
                    await _handoff.TransitionToGateAsync(duress.Session, ct).ConfigureAwait(true);
                    break;

                default:
                    ErrorMessage = "Incorrect password. Please try again.";
                    FailedAttemptNonce++;
                    break;
            }
        }
        finally
        {
            candidate.Zeroize();
            IsBusy = false;
        }
    }

    /// <summary>
    /// Single-persona auto-start bypass: when exactly one persona flagged "Always auto-start
    /// with this persona on unlock", lease its port and skip straight to the shell.
    /// Falls back to the gate when nothing is flagged or the port cannot be reserved.
    /// </summary>
    private async Task TryAutoStartLaunchAsync(IAuthenticatedSession session, CancellationToken ct)
    {
        var entries = await _personas.ListAsync(session, ct).ConfigureAwait(true);
        var autoStart = entries.Count(e => e.AutoStart) == 1
            ? entries.First(e => e.AutoStart)
            : null;

        if (autoStart is not null)
        {
            var lease = await _ports
                .AcquireAsync(autoStart.LastBoundPort ?? autoStart.PreferredPort, ct)
                .ConfigureAwait(true);
            if (lease is not null)
            {
                var descriptor = new PersonaLaunchDescriptor(
                    autoStart.IdentityId, autoStart.DisplayName, lease.Port, lease);
                try
                {
                    await _handoff
                        .TransitionToMainShellAsync(session, new[] { descriptor }, ct: ct)
                        .ConfigureAwait(true);
                    return;
                }
                catch
                {
                    // Handoff owns the lease only after a successful transition — release it
                    // here so no bound socket lingers ([DEP-DESKTOP-PRE-ACTIVATION-STEALTH]).
                    lease.Dispose();
                    throw;
                }
            }
        }

        await _handoff.TransitionToGateAsync(session, ct).ConfigureAwait(true);
    }
}

// SPECIFICATION GOVERNANCE: Read Percolator.Desktop/spec/ViewModel.spec.md before modifying this file.

using System;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Percolator.Application2.Ports;
using Percolator.Desktop.Common.Windowing;
using Percolator.Desktop.ViewModels;
using Percolator.Domain.Security.ValueObjects;

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
        IWindowHandoffCoordinator handoff)
    {
        _unlockCoordinator = unlockCoordinator ?? throw new ArgumentNullException(nameof(unlockCoordinator));
        _handoff = handoff ?? throw new ArgumentNullException(nameof(handoff));
    }

    [RelayCommand]
    private async Task UnlockAsync(CancellationToken ct)
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
                case UnlockResult.Success success:
                    await _handoff.TransitionToGateAsync(success.Session, ct).ConfigureAwait(true);
                    break;

                case UnlockResult.DuressActivated:
                    // Silent decoy transition: Screen 2 empty-vault state, zero error surfacing.
                    await _handoff.TransitionToGateAsync(null, ct).ConfigureAwait(true);
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
}

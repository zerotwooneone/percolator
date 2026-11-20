// SPECIFICATION GOVERNANCE: Read Percolator.Desktop/spec/ViewModel.spec.md before modifying this file.

using System;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Percolator.Application.Ports;
using Percolator.Desktop.Common.Services;
using Percolator.Desktop.Common.Windowing;
using Percolator.Desktop.ViewModels;
using Percolator.Domain.Security.ValueObjects;

namespace Percolator.Desktop.Features.Startup.ViewModels;

/// <summary>
/// ViewModel for State 1A (first-time vault setup): master + confirm password fields,
/// progressive-disclosure duress passphrase, and vault initialization via
/// <see cref="IDatabaseUnlockCoordinator.InitializeAsync"/>.
/// </summary>
public partial class VaultSetupViewModel : ViewModelBase
{
    private readonly IDatabaseUnlockCoordinator _unlockCoordinator;
    private readonly IWindowHandoffCoordinator _handoff;
    private readonly IDialogService _dialogs;

    [ObservableProperty]
    private string _masterPassword = string.Empty;

    [ObservableProperty]
    private string _confirmPassword = string.Empty;

    [ObservableProperty]
    private string _duressPassword = string.Empty;

    /// <summary>Progressive disclosure accordion for the optional duress passphrase.</summary>
    [ObservableProperty]
    private bool _isDuressExpanded;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string? _errorMessage;

    public VaultSetupViewModel(
        IDatabaseUnlockCoordinator unlockCoordinator,
        IWindowHandoffCoordinator handoff,
        IDialogService dialogs)
    {
        _unlockCoordinator = unlockCoordinator ?? throw new ArgumentNullException(nameof(unlockCoordinator));
        _handoff = handoff ?? throw new ArgumentNullException(nameof(handoff));
        _dialogs = dialogs ?? throw new ArgumentNullException(nameof(dialogs));
    }

    [RelayCommand]
    private void ToggleDuressExpanded()
    {
        IsDuressExpanded = !IsDuressExpanded;
    }

    [RelayCommand]
    private async Task ContinueAsync(CancellationToken ct)
    {
        if (IsBusy)
        {
            return;
        }

        ErrorMessage = null;

        if (IsWhitespaceOnly(MasterPassword))
        {
            ErrorMessage = "Password cannot be only whitespace.";
            return;
        }

        if (MasterPassword != ConfirmPassword)
        {
            ErrorMessage = "Passwords do not match.";
            return;
        }

        if (IsWhitespaceOnly(DuressPassword))
        {
            ErrorMessage = "Duress passphrase cannot be only whitespace.";
            return;
        }

        if (!string.IsNullOrEmpty(DuressPassword) && DuressPassword == MasterPassword)
        {
            ErrorMessage = "Duress passphrase cannot match the master password.";
            return;
        }

        // Blank master password requires explicit confirmation before initializing.
        if (MasterPassword.Length == 0)
        {
            var result = await _dialogs.ShowAsync(new DialogRequest(
                "Skip password protection?",
                "Your vault will still be encrypted with a random key, but that key will be accessible without a password.",
                "Continue Without Password",
                "Cancel"), ct).ConfigureAwait(true);

            if (result != DialogResult.Primary)
            {
                return;
            }
        }

        IsBusy = true;
        var master = VaultPassphrase.Create(MasterPassword);
        var duress = string.IsNullOrEmpty(DuressPassword) ? null : VaultPassphrase.Create(DuressPassword);
        try
        {
            var result = await _unlockCoordinator.InitializeAsync(master, duress, ct).ConfigureAwait(true);
            if (result.IsFailure)
            {
                ErrorMessage = result.Error.Description;
                return;
            }

            await _handoff.TransitionToGateAsync(result.Value, ct).ConfigureAwait(true);
        }
        finally
        {
            master.Zeroize();
            duress?.Zeroize();
            IsBusy = false;
        }
    }

    private static bool IsWhitespaceOnly(string value) =>
        value.Length > 0 && string.IsNullOrWhiteSpace(value);
}

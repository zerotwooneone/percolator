// SPECIFICATION GOVERNANCE: Read Percolator.Desktop/spec/ViewModel.spec.md before modifying this file.

using System;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Percolator.Desktop.ViewModels;

namespace Percolator.Desktop.Features.Startup.ViewModels;

/// <summary>
/// "Stop using this Exchange?" confirmation for a persona unlink ([INV-DESKTOP-PRESENTATION-MODELS]).
/// The copy states plainly that nothing leaves the device — held envelopes stay on the
/// exchange until they expire. Self-hosted exchanges additionally offer a verifiable local
/// purge of the vault-resident queue. The host window closes when <see cref="RequestClose"/>
/// fires and reads <see cref="Result"/>.
/// </summary>
public partial class StopUsingExchangeDialogViewModel : ViewModelBase
{
    /// <summary>Raised when the dialog should close (confirm or cancel); the window owns closing.</summary>
    public event EventHandler? RequestClose;

    /// <summary><c>true</c> when the user confirmed the unlink; <c>false</c> on cancel/close.</summary>
    public bool Result { get; private set; }

    /// <summary>The persona that will stop collecting mail.</summary>
    public string PersonaName { get; private set; } = string.Empty;

    /// <summary>The exchange being unlinked from.</summary>
    public string ExchangeName { get; private set; } = string.Empty;

    /// <summary>Body copy — no wire traffic, held envelopes persist until expiry.</summary>
    public string Message =>
        $"{PersonaName} will stop collecting mail through {ExchangeName}. This only changes " +
        "things on this device — nothing is sent. Anything the Exchange is already holding " +
        "stays there until it expires, and there's no way to recall it.\n\n" +
        "To erase all trust material permanently, shred the persona instead.";

    /// <summary>Only self-hosted exchanges expose the local queue purge option.</summary>
    public bool CanPurgeQueue { get; private set; }

    /// <summary>User opt-in: also empty this mailbox's stored envelopes (self-hosted only).</summary>
    [ObservableProperty]
    private bool _purgeQueuedEnvelopes;

    /// <summary>Seeds the dialog before it opens.</summary>
    public void Initialize(string personaName, string exchangeName, bool canPurgeQueue)
    {
        PersonaName = personaName ?? string.Empty;
        ExchangeName = exchangeName ?? string.Empty;
        CanPurgeQueue = canPurgeQueue;
        PurgeQueuedEnvelopes = false;
        Result = false;
        OnPropertyChanged(nameof(PersonaName));
        OnPropertyChanged(nameof(ExchangeName));
        OnPropertyChanged(nameof(Message));
        OnPropertyChanged(nameof(CanPurgeQueue));
    }

    /// <summary>[ Stop using ] — confirms the local revocation.</summary>
    [RelayCommand]
    private void StopUsing()
    {
        Result = true;
        RequestClose?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void Cancel()
    {
        Result = false;
        RequestClose?.Invoke(this, EventArgs.Empty);
    }
}

// SPECIFICATION GOVERNANCE: Read Percolator.Desktop/spec/ViewModel.spec.md before modifying this file.

using System;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Percolator.Desktop.Common.Windowing;
using Percolator.Desktop.Features.Startup.Models;
using Percolator.Desktop.ViewModels;

namespace Percolator.Desktop.Features.Startup.ViewModels;

/// <summary>
/// "Stop using this Exchange?" confirmation for a persona unlink ([INV-DESKTOP-PRESENTATION-MODELS]).
/// The copy states plainly that nothing leaves the device — held envelopes stay on the
/// exchange until they expire. Self-hosted exchanges additionally offer a verifiable local
/// purge of the vault-resident queue.
/// </summary>
public partial class StopUsingExchangeDialogViewModel : ViewModelBase
{
    private readonly IDialogContext<StopUsingExchangeInput, StopUsingExchangeResult> _dialogContext;

    public StopUsingExchangeDialogViewModel(IDialogContext<StopUsingExchangeInput, StopUsingExchangeResult> dialogContext)
    {
        _dialogContext = dialogContext ?? throw new ArgumentNullException(nameof(dialogContext));

        var input = dialogContext.Data;
        PersonaName = input?.PersonaName ?? string.Empty;
        ExchangeName = input?.ExchangeName ?? string.Empty;
        CanPurgeQueue = input?.CanPurgeQueue ?? false;
    }

    /// <summary>The persona that will stop collecting mail.</summary>
    public string PersonaName { get; }

    /// <summary>The exchange being unlinked from.</summary>
    public string ExchangeName { get; }

    /// <summary>Body copy — no wire traffic, held envelopes persist until expiry.</summary>
    public string Message =>
        $"{PersonaName} will stop collecting mail through {ExchangeName}. This only changes " +
        "things on this device — nothing is sent. Anything the Exchange is already holding " +
        "stays there until it expires, and there's no way to recall it.\n\n" +
        "To erase all trust material permanently, shred the persona instead.";

    /// <summary>Only self-hosted exchanges expose the local queue purge option.</summary>
    public bool CanPurgeQueue { get; }

    /// <summary>User opt-in: also empty this mailbox's stored envelopes (self-hosted only).</summary>
    [ObservableProperty]
    private bool _purgeQueuedEnvelopes;

    /// <summary>[ Stop using ] — confirms the local revocation.</summary>
    [RelayCommand]
    private void StopUsing()
    {
        _dialogContext.Close(new StopUsingExchangeResult(Confirmed: true, PurgeQueuedEnvelopes: PurgeQueuedEnvelopes));
    }

    [RelayCommand]
    private void Cancel()
    {
        _dialogContext.Cancel();
    }
}

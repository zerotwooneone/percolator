// SPECIFICATION GOVERNANCE: Read Percolator.Desktop/spec/ViewModel.spec.md before modifying this file.

using System;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Percolator.Desktop.Common.Windowing;
using Percolator.Desktop.ViewModels;

namespace Percolator.Desktop.Features.Startup.ViewModels;

/// <summary>
/// ViewModel for the destructive persona-delete dialog ([INV-DESKTOP-PRESENTATION-MODELS]).
/// "Shred & Delete Persona" stays disabled until the typed confirmation exactly matches
/// the persona name.
/// </summary>
public partial class DeletePersonaDialogViewModel : ViewModelBase
{
    private readonly IDialogContext<string, bool> _dialogContext;
    private readonly string _personaName;

    public DeletePersonaDialogViewModel(IDialogContext<string, bool> dialogContext)
    {
        _dialogContext = dialogContext ?? throw new ArgumentNullException(nameof(dialogContext));
        _personaName = dialogContext.Data ?? string.Empty;
    }

    /// <summary>The persona name the user must re-type to unlock the danger button.</summary>
    public string PersonaName => _personaName;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConfirmDeleteCommand))]
    private string _confirmation = string.Empty;

    /// <summary>[ Shred & Delete Persona ] — enabled only on an exact ordinal match.</summary>
    [RelayCommand(CanExecute = nameof(CanDelete))]
    private void ConfirmDelete()
    {
        _dialogContext.Close(true);
    }

    [RelayCommand]
    private void Cancel()
    {
        _dialogContext.Cancel();
    }

    private bool CanDelete() =>
        string.Equals(Confirmation, _personaName, StringComparison.Ordinal);
}

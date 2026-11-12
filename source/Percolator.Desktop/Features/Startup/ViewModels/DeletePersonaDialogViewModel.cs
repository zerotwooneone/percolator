// SPECIFICATION GOVERNANCE: Read Percolator.Desktop/spec/ViewModel.spec.md before modifying this file.

using System;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Percolator.Desktop.ViewModels;

namespace Percolator.Desktop.Features.Startup.ViewModels;

/// <summary>
/// ViewModel for the destructive persona-delete dialog ([INV-DESKTOP-PRESENTATION-MODELS]).
/// "Shred & Delete Persona" stays disabled until the typed confirmation exactly matches
/// the persona name. The host window closes when <see cref="RequestClose"/> fires and
/// reads <see cref="Result"/>.
/// </summary>
public partial class DeletePersonaDialogViewModel : ViewModelBase
{
    private string _personaName = string.Empty;

    /// <summary>Raised when the dialog should close (confirm or cancel); the window owns closing.</summary>
    public event EventHandler? RequestClose;

    /// <summary><c>true</c> when the user confirmed deletion; <c>false</c> on cancel/close.</summary>
    public bool Result { get; private set; }

    /// <summary>The persona name the user must re-type to unlock the danger button.</summary>
    public string PersonaName => _personaName;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConfirmDeleteCommand))]
    private string _confirmation = string.Empty;

    /// <summary>Seeds the expected confirmation text before the dialog opens.</summary>
    public void Initialize(string personaName)
    {
        _personaName = personaName ?? string.Empty;
        Confirmation = string.Empty;
        OnPropertyChanged(nameof(PersonaName));
    }

    /// <summary>[ Shred & Delete Persona ] — enabled only on an exact ordinal match.</summary>
    [RelayCommand(CanExecute = nameof(CanDelete))]
    private void ConfirmDelete()
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

    private bool CanDelete() =>
        string.Equals(Confirmation, _personaName, StringComparison.Ordinal);
}

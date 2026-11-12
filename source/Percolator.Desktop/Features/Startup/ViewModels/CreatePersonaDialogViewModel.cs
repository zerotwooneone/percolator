// SPECIFICATION GOVERNANCE: Read Percolator.Desktop/spec/ViewModel.spec.md before modifying this file.

using System;
using System.ComponentModel.DataAnnotations;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Percolator.Desktop.Common.Services;
using Percolator.Desktop.Common.Validation;
using Percolator.Desktop.Features.Startup.Models;
using Percolator.Desktop.ViewModels;
using Percolator.Domain.Identities.ValueObjects;

namespace Percolator.Desktop.Features.Startup.ViewModels;

/// <summary>
/// ViewModel for the create/edit persona dialog ([INV-DESKTOP-PRESENTATION-MODELS]).
/// Create mode generates a candidate identity whose deterministic identicon the avatar
/// picker previews; "re-roll" samples a new identity. Edit mode pins the existing
/// identity — the avatar cannot change without changing the persona.
/// The host window closes when <see cref="RequestClose"/> fires and reads <see cref="Result"/>.
/// </summary>
public partial class CreatePersonaDialogViewModel : ViewModelBase
{
    private readonly IIdenticonGenerator _identicons;
    private PublicIdentityId _candidateId;
    private bool _isEdit;

    public CreatePersonaDialogViewModel(IIdenticonGenerator identicons)
    {
        _identicons = identicons ?? throw new ArgumentNullException(nameof(identicons));
        InitializeForCreate();
    }

    /// <summary>Raised when the dialog should close (save or cancel); the window owns closing.</summary>
    public event EventHandler? RequestClose;

    /// <summary>Set on save; <c>null</c> means the dialog was cancelled or closed.</summary>
    public PersonaEditorResult? Result { get; private set; }

    [ObservableProperty]
    private string _title = "Create Persona";

    [ObservableProperty]
    private string _primaryText = "Create Persona";

    [ObservableProperty]
    private AvatarPresentation? _avatar;

    /// <summary>Avatar re-roll is only meaningful while creating (identity not yet persisted).</summary>
    [ObservableProperty]
    private bool _canRerollAvatar = true;

    [ObservableProperty]
    [NotWhitespace(ErrorMessage = "Display name is required.")]
    [MaxLength(32, ErrorMessage = "Display name must be 32 characters or fewer.")]
    [NotifyDataErrorInfo]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    private string _displayName = string.Empty;

    [ObservableProperty]
    [Range(1024, 65535, ErrorMessage = "Port must be between 1024 and 65535.")]
    [NotifyDataErrorInfo]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    private int _preferredPort = PersonaDirectoryService.DefaultPreferredPort;

    /// <summary>Create mode: a fresh candidate identity seeds the preview identicon.</summary>
    public void InitializeForCreate()
    {
        _isEdit = false;
        _candidateId = PublicIdentityId.New();
        Title = "Create Persona";
        PrimaryText = "Create Persona";
        CanRerollAvatar = true;
        Avatar = AvatarPresentation.FromIdenticon(_identicons.Generate(_candidateId.Value));
    }

    /// <summary>Edit mode: the existing identity pins the identicon; only name and port are editable.</summary>
    public void InitializeForEdit(string displayName, int preferredPort, Guid identityId)
    {
        _isEdit = true;
        _candidateId = new PublicIdentityId(identityId);
        Title = "Edit Profile";
        PrimaryText = "Save Changes";
        CanRerollAvatar = false;
        Avatar = AvatarPresentation.FromIdenticon(_identicons.Generate(identityId));
        DisplayName = displayName;
        PreferredPort = preferredPort;
    }

    /// <summary>Avatar picker: samples a new candidate identity and re-renders the preview.</summary>
    [RelayCommand]
    private void RerollAvatar()
    {
        _candidateId = PublicIdentityId.New();
        Avatar = AvatarPresentation.FromIdenticon(_identicons.Generate(_candidateId.Value));
    }

    [RelayCommand(CanExecute = nameof(CanSave))]
    private void Save()
    {
        Result = new PersonaEditorResult(
            _isEdit ? null : _candidateId,
            DisplayName.Trim(),
            PreferredPort);
        RequestClose?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void Cancel()
    {
        Result = null;
        RequestClose?.Invoke(this, EventArgs.Empty);
    }

    private bool CanSave() => !HasErrors;
}

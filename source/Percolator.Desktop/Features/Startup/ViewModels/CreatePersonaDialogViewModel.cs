// SPECIFICATION GOVERNANCE: Read Percolator.Desktop/spec/ViewModel.spec.md before modifying this file.

using System;
using System.ComponentModel.DataAnnotations;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Percolator.Desktop.Common.Services;
using Percolator.Desktop.Common.Validation;
using Percolator.Desktop.Common.Windowing;
using Percolator.Desktop.Features.Startup.Models;
using Percolator.Desktop.ViewModels;
using Percolator.Domain.Identities.ValueObjects;
using PublicIdentityId = Percolator.Domain.Identities.PublicIdentityId;

namespace Percolator.Desktop.Features.Startup.ViewModels;

/// <summary>
/// ViewModel for the create/edit persona dialog ([INV-DESKTOP-PRESENTATION-MODELS]).
/// Create mode generates a candidate identity whose deterministic identicon the avatar
/// picker previews; "re-roll" samples a new identity. Edit mode pins the existing
/// identity — the avatar cannot change without changing the persona.
/// The host window closes when the injected dialog context completes.
/// </summary>
public partial class CreatePersonaDialogViewModel : ViewModelBase
{
    private readonly IDialogContext<PersonaEditorInput, PersonaEditorResult?> _dialogContext;
    private readonly IIdenticonGenerator _identicons;
    private PublicIdentityId _candidateId;
    private bool _isEdit;

    public CreatePersonaDialogViewModel(
        IDialogContext<PersonaEditorInput, PersonaEditorResult?> dialogContext,
        IIdenticonGenerator identicons)
    {
        _dialogContext = dialogContext ?? throw new ArgumentNullException(nameof(dialogContext));
        _identicons = identicons ?? throw new ArgumentNullException(nameof(identicons));

        var input = dialogContext.Data;
        if (input is not null && input.IsEdit && input.IdentityId.HasValue)
        {
            InitializeForEdit(input.DisplayName ?? string.Empty, input.PreferredPort ?? PersonaDirectoryService.DefaultPreferredPort, input.IdentityId.Value);
        }
        else
        {
            InitializeForCreate();
        }
    }


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
    public void InitializeForEdit(string displayName, int preferredPort, PublicIdentityId identityId)
    {
        _isEdit = true;
        _candidateId = identityId;
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
        var result = new PersonaEditorResult(
            _isEdit ? null : _candidateId,
            DisplayName.Trim(),
            PreferredPort);
        _dialogContext.Close(result);
    }

    [RelayCommand]
    private void Cancel()
    {
        _dialogContext.Cancel();
    }

    private bool CanSave() => !HasErrors;
}

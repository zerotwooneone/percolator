// SPECIFICATION GOVERNANCE: Read Percolator.Desktop/spec/ViewModel.spec.md before modifying this file.

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Percolator.Application.Delivery.Ports;
using Percolator.Desktop.Common.Services;
using Percolator.Desktop.Common.Validation;
using Percolator.Desktop.Common.Windowing;
using Percolator.Desktop.Features.Startup.Models;
using Percolator.Desktop.ViewModels;
using Percolator.Domain.Delivery.ValueObjects;
using Percolator.Domain.Identities.ValueObjects;
using PublicIdentityId = Percolator.Domain.Identities.PublicIdentityId;

namespace Percolator.Desktop.Features.Startup.ViewModels;

/// <summary>
/// Presentation model for an individual connected relay item inside the persona customize dialog.
/// </summary>
public sealed partial class PersonaRelayItemViewModel : ObservableObject
{
    public PublicIdentityId RelayId { get; }
    public string DisplayName { get; }
    public string Endpoint { get; }
    public bool IsLocalHost { get; }
    public RelayLinkStatus InitialStatus { get; }

    [ObservableProperty]
    private bool _isEnabled;

    public bool HasChanged => IsEnabled != (InitialStatus == RelayLinkStatus.Enabled);

    public PersonaRelayItemViewModel(PersonaRelayLinkReadModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        RelayId = model.RelayIdentityId;
        DisplayName = model.DisplayName;
        Endpoint = model.Endpoint.Value;
        IsLocalHost = model.IsLocalRelayHost;
        InitialStatus = model.Status;
        _isEnabled = model.Status == RelayLinkStatus.Enabled;
    }
}

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
    private readonly IPersonaRelayQueryService _relayQuery;
    private readonly IRelayDirectory _relayDirectory;

    private readonly ObservableCollection<PersonaRelayItemViewModel> _connectedRelays = new();
    public ReadOnlyObservableCollection<PersonaRelayItemViewModel> ConnectedRelays { get; }

    private PublicIdentityId _candidateId;
    private bool _isEdit;

    public CreatePersonaDialogViewModel(
        IDialogContext<PersonaEditorInput, PersonaEditorResult?> dialogContext,
        IIdenticonGenerator identicons,
        IPersonaRelayQueryService relayQuery,
        IRelayDirectory relayDirectory)
    {
        _dialogContext = dialogContext ?? throw new ArgumentNullException(nameof(dialogContext));
        _identicons = identicons ?? throw new ArgumentNullException(nameof(identicons));
        _relayQuery = relayQuery ?? throw new ArgumentNullException(nameof(relayQuery));
        _relayDirectory = relayDirectory ?? throw new ArgumentNullException(nameof(relayDirectory));

        ConnectedRelays = new ReadOnlyObservableCollection<PersonaRelayItemViewModel>(_connectedRelays);

        var input = dialogContext.Data;
        if (input is not null && input.IsEdit && input.IdentityId.HasValue)
        {
            InitializeForEdit(input.DisplayName ?? string.Empty, input.PreferredPort ?? PersonaDirectoryService.DefaultPreferredPort, input.IdentityId.Value);
        }
        else
        {
            InitializeForCreate(input?.PreferredPort);
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
    private bool _hasConnectedRelays;

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
    public void InitializeForCreate(int? preferredPort = null)
    {
        _isEdit = false;
        _candidateId = PublicIdentityId.New();
        Title = "Create Persona";
        PrimaryText = "Create Persona";
        CanRerollAvatar = true;
        HasConnectedRelays = false;
        _connectedRelays.Clear();
        Avatar = AvatarPresentation.FromIdenticon(_identicons.Generate(_candidateId.Value));
        PreferredPort = preferredPort ?? PersonaDirectoryService.DefaultPreferredPort;
    }

    /// <summary>Edit mode: the existing identity pins the identicon; only name, port, and relay links are editable.</summary>
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

        _ = LoadRelaysAsync(identityId);
    }

    /// <summary>Asynchronously loads connected relays via the CQRS query port.</summary>
    public async Task LoadRelaysAsync(PublicIdentityId identityId, CancellationToken ct = default)
    {
        try
        {
            var links = await _relayQuery.GetRelayLinksForPersonaAsync(identityId, ct).ConfigureAwait(true);
            _connectedRelays.Clear();
            foreach (var link in links)
            {
                _connectedRelays.Add(new PersonaRelayItemViewModel(link));
            }
            HasConnectedRelays = _connectedRelays.Count > 0;
        }
        catch
        {
            // Query failure is non-fatal for editing basic profile properties
        }
    }

    /// <summary>Avatar picker: samples a new candidate identity and re-renders the preview.</summary>
    [RelayCommand]
    private void RerollAvatar()
    {
        _candidateId = PublicIdentityId.New();
        Avatar = AvatarPresentation.FromIdenticon(_identicons.Generate(_candidateId.Value));
    }

    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task SaveAsync(CancellationToken ct)
    {
        if (_isEdit)
        {
            foreach (var item in _connectedRelays.Where(r => r.HasChanged))
            {
                var newStatus = item.IsEnabled ? RelayLinkStatus.Enabled : RelayLinkStatus.Disabled;
                await _relayDirectory.SetMailboxBindingStatusAsync(item.RelayId, _candidateId, newStatus, ct).ConfigureAwait(true);
            }
        }

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

// SPECIFICATION GOVERNANCE: Read Percolator.Desktop/spec/ViewModel.spec.md before modifying this file.

using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Percolator.Desktop.Common.Services;
using Percolator.Desktop.Common.Validation;
using Percolator.Desktop.Features.Startup.Models;
using Percolator.Desktop.ViewModels;
using Percolator.Domain.Identities.ValueObjects;

namespace Percolator.Desktop.Features.Startup.ViewModels;

/// <summary>
/// Three-step exchange creation wizard ([INV-DESKTOP-PRESENTATION-MODELS]):
/// (1) advanced-feature framing + uptime warning, (2) display name + avatar + preferred port
/// mirroring persona creation, (3) optional persona linking — every checkbox ships
/// <em>unchecked</em> because linking has infrastructure-linkability semantics and must be
/// explicit opt-in. The host window closes when <see cref="RequestClose"/> fires and reads
/// <see cref="Result"/>; the created tile appears offline and inert.
/// </summary>
public partial class ExchangeCreationViewModel : ViewModelBase
{
    private readonly IIdenticonGenerator _identicons;
    private PublicIdentityId _candidateId;

    public ExchangeCreationViewModel(IIdenticonGenerator identicons)
    {
        _identicons = identicons ?? throw new ArgumentNullException(nameof(identicons));
        Initialize(Array.Empty<PersonaDirectoryEntry>());
    }

    /// <summary>Raised when the dialog should close (finish or cancel); the window owns closing.</summary>
    public event EventHandler? RequestClose;

    /// <summary>Set on finish; <c>null</c> means the wizard was cancelled or closed.</summary>
    public ExchangeCreationResult? Result { get; private set; }

    /// <summary>0 = framing, 1 = profile, 2 = persona linking (optional, skippable).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFramingStep))]
    [NotifyPropertyChangedFor(nameof(IsProfileStep))]
    [NotifyPropertyChangedFor(nameof(IsLinkingStep))]
    [NotifyPropertyChangedFor(nameof(CurrentStepLabel))]
    [NotifyPropertyChangedFor(nameof(ShowNext))]
    [NotifyCanExecuteChangedFor(nameof(NextCommand))]
    [NotifyCanExecuteChangedFor(nameof(BackCommand))]
    private int _currentStep;

    /// <summary>
    /// Remote-registration mode replaces the wizard steps with a single endpoint + relay
    /// identity form — a vault-local row write, no connection is opened.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFramingStep))]
    [NotifyPropertyChangedFor(nameof(IsProfileStep))]
    [NotifyPropertyChangedFor(nameof(IsLinkingStep))]
    [NotifyPropertyChangedFor(nameof(IsRemoteStep))]
    [NotifyPropertyChangedFor(nameof(CurrentStepLabel))]
    [NotifyPropertyChangedFor(nameof(ShowNext))]
    [NotifyCanExecuteChangedFor(nameof(NextCommand))]
    [NotifyCanExecuteChangedFor(nameof(BackCommand))]
    [NotifyCanExecuteChangedFor(nameof(FinishCommand))]
    private bool _isRemoteMode;

    public bool IsFramingStep => !IsRemoteMode && CurrentStep == 0;
    public bool IsProfileStep => !IsRemoteMode && CurrentStep == 1;
    public bool IsLinkingStep => !IsRemoteMode && CurrentStep == 2;
    public bool IsRemoteStep => IsRemoteMode;

    /// <summary>Remote mode has no Next — it is a single form.</summary>
    public bool ShowNext => !IsRemoteMode && !IsLinkingStep;

    /// <summary>Step caption shown in the dialog header.</summary>
    public string CurrentStepLabel => IsRemoteMode
        ? "Add an existing exchange"
        : CurrentStep switch
        {
            0 => "Run your own post-office",
            1 => "Exchange profile",
            _ => "Link personas (optional)"
        };

    /// <summary>The deliberate self-selection framing + honest uptime warning (step 1).</summary>
    public string FramingMessage =>
        "Run your own post-office — an advanced feature. It's only open while Percolator " +
        "is unlocked and this device is awake.";

    /// <summary>The opt-in linking copy for step 3.</summary>
    public string LinkingMessage =>
        "Linked personas will route offline delivery through this exchange.";

    [ObservableProperty]
    private AvatarPresentation? _avatar;

    [ObservableProperty]
    [NotWhitespace(ErrorMessage = "Display name is required.")]
    [MaxLength(32, ErrorMessage = "Display name must be 32 characters or fewer.")]
    [NotifyDataErrorInfo]
    [NotifyCanExecuteChangedFor(nameof(NextCommand))]
    [NotifyCanExecuteChangedFor(nameof(FinishCommand))]
    private string _displayName = string.Empty;

    [ObservableProperty]
    [Range(1024, 65535, ErrorMessage = "Port must be between 1024 and 65535.")]
    [NotifyDataErrorInfo]
    [NotifyCanExecuteChangedFor(nameof(NextCommand))]
    [NotifyCanExecuteChangedFor(nameof(FinishCommand))]
    private int _preferredPort = ExchangeDirectoryService.DefaultPreferredPort;

    /// <summary>Remote form: network address of the exchange (host:port or URI).</summary>
    [ObservableProperty]
    [NotWhitespace(ErrorMessage = "Endpoint is required.")]
    [NotifyDataErrorInfo]
    [NotifyCanExecuteChangedFor(nameof(FinishCommand))]
    private string _remoteEndpoint = string.Empty;

    /// <summary>Remote form: relay identity GUID shared out-of-band by the operator.</summary>
    [ObservableProperty]
    [NotWhitespace(ErrorMessage = "Relay identity is required.")]
    [NotifyDataErrorInfo]
    [NotifyCanExecuteChangedFor(nameof(FinishCommand))]
    private string _remoteRelayId = string.Empty;

    /// <summary>Inline error surfaced when the remote form fails validation.</summary>
    [ObservableProperty]
    private string? _remoteError;

    /// <summary>Personas offered for linking — all unchecked by default (explicit opt-in).</summary>
    public IReadOnlyList<LinkablePersonaModel> LinkablePersonas { get; private set; } =
        Array.Empty<LinkablePersonaModel>();

    /// <summary>Resets the wizard and seeds the persona checkbox list (all unchecked).</summary>
    public void Initialize(IReadOnlyList<PersonaDirectoryEntry> personas)
    {
        ArgumentNullException.ThrowIfNull(personas);

        Result = null;
        _candidateId = PublicIdentityId.New();
        Avatar = AvatarPresentation.FromIdenticon(_identicons.Generate(_candidateId.Value));
        DisplayName = string.Empty;
        PreferredPort = ExchangeDirectoryService.DefaultPreferredPort;
        IsRemoteMode = false;
        RemoteEndpoint = string.Empty;
        RemoteRelayId = string.Empty;
        RemoteError = null;
        // Eager validation: an untouched empty name must block Next/Finish on the profile step.
        ValidateAllProperties();
        LinkablePersonas = personas
            .Select(p => new LinkablePersonaModel(
                p.IdentityId,
                p.DisplayName,
                AvatarPresentation.FromIdenticon(_identicons.Generate(p.IdentityId))))
            .ToList();
        OnPropertyChanged(nameof(LinkablePersonas));
        CurrentStep = 0;
    }

    /// <summary>Avatar picker: samples a new candidate relay identity and re-renders the preview.</summary>
    [RelayCommand]
    private void RerollAvatar()
    {
        _candidateId = PublicIdentityId.New();
        Avatar = AvatarPresentation.FromIdenticon(_identicons.Generate(_candidateId.Value));
    }

    [RelayCommand(CanExecute = nameof(CanGoNext))]
    private void Next() => CurrentStep++;

    [RelayCommand(CanExecute = nameof(CanGoBack))]
    private void Back()
    {
        if (IsRemoteMode)
        {
            IsRemoteMode = false;
            return;
        }

        CurrentStep--;
    }

    /// <summary>Framing-step escape: switch to the remote-registration form.</summary>
    [RelayCommand]
    private void UseRemote() => IsRemoteMode = true;

    /// <summary>
    /// Finish is available once the profile step validates — the linking step is optional
    /// and skippable, so it is not required to reach step three first. In remote mode the
    /// relay identity must parse as a GUID before the result is produced.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanFinish))]
    private void Finish()
    {
        if (IsRemoteMode)
        {
            RemoteError = null;
            if (!Guid.TryParse(RemoteRelayId.Trim(), out var remoteId))
            {
                RemoteError = "Relay identity must be a GUID — the exchange operator shares it out-of-band.";
                return;
            }

            Result = new ExchangeCreationResult(
                new PublicIdentityId(remoteId),
                string.Empty,
                0,
                Array.Empty<PublicIdentityId>(),
                IsRemote: true,
                RemoteEndpoint: RemoteEndpoint.Trim());
            RequestClose?.Invoke(this, EventArgs.Empty);
            return;
        }

        Result = new ExchangeCreationResult(
            _candidateId,
            DisplayName.Trim(),
            PreferredPort,
            LinkablePersonas
                .Where(p => p.IsLinked)
                .Select(p => new PublicIdentityId(p.PersonaId))
                .ToList());
        RequestClose?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void Cancel()
    {
        Result = null;
        RequestClose?.Invoke(this, EventArgs.Empty);
    }

    private bool CanGoNext() =>
        !IsRemoteMode && CurrentStep < 2 && (CurrentStep == 0 || !HasErrors);

    private bool CanGoBack() => IsRemoteMode || CurrentStep > 0;

    private bool CanFinish() =>
        IsRemoteMode
            ? !string.IsNullOrWhiteSpace(RemoteEndpoint) && !string.IsNullOrWhiteSpace(RemoteRelayId)
            : CurrentStep >= 1 && !HasErrors;
}

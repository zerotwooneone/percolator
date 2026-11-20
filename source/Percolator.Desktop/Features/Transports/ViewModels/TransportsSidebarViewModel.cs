// SPECIFICATION GOVERNANCE: Read Percolator.Desktop/spec/ViewModel.spec.md before modifying this file.

namespace Percolator.Desktop.Features.Transports.ViewModels;

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using ObservableCollections;
using Percolator.Application.Session.Ports;
using Percolator.Desktop.Common.Navigation;
using Percolator.Desktop.Common.Services;
using Percolator.Desktop.Common.Windowing;
using Percolator.Desktop.Features.Shell.Models;
using Percolator.Desktop.Features.Transports.Models;
using Percolator.Desktop.Features.Transports.Services;
using Percolator.Desktop.ViewModels;
using Percolator.Domain.Delivery.ValueObjects;
using Percolator.Domain.Identities;
using Percolator.Domain.Identities.ValueObjects;
using R3;

/// <summary>
/// ViewModel governing the 72px fixed Left Rail navigation sidebar ([DESK-05], [NAV-01]).
/// Presents Direct Messages button, active Relay list (post offices), Add Connection dialog trigger,
/// pull-based Pending Requests notification light, and Persona avatar profile trigger.
/// </summary>
public sealed partial class TransportsSidebarViewModel : ViewModelBase
{
    private readonly IShellNavigationStateService _navigationService;
    private readonly IRelayStateService _relayStateService;
    private readonly IInboundRequestStateService _inboundRequestService;
    private readonly ICurrentPersonaService _currentPersonaService;
    private readonly IWindowManager _windowManager;
    private readonly ILogger<TransportsSidebarViewModel> _logger;

    public TransportsSidebarViewModel(
        IShellNavigationStateService navigationService,
        IRelayStateService relayStateService,
        IInboundRequestStateService inboundRequestService,
        ICurrentPersonaService currentPersonaService,
        IWindowManager windowManager,
        ILogger<TransportsSidebarViewModel> logger)
    {
        _navigationService = navigationService ?? throw new ArgumentNullException(nameof(navigationService));
        _relayStateService = relayStateService ?? throw new ArgumentNullException(nameof(relayStateService));
        _inboundRequestService = inboundRequestService ?? throw new ArgumentNullException(nameof(inboundRequestService));
        _currentPersonaService = currentPersonaService ?? throw new ArgumentNullException(nameof(currentPersonaService));
        _windowManager = windowManager ?? throw new ArgumentNullException(nameof(windowManager));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        var view = _relayStateService.Relays;
        Relays = SynchronizationContext.Current != null
            ? view.ToNotifyCollectionChanged(SynchronizationContextCollectionEventDispatcher.Current)
            : view.ToNotifyCollectionChanged();
        Relays.AddTo(ref Bag);

        ActiveTarget = _navigationService.ActiveLeftRailTarget;

        _inboundRequestService.PendingCount
            .ObserveOnCurrentSynchronizationContext()
            .Subscribe(count =>
            {
                PendingRequestsCount = count;
                HasPendingRequests = count > 0;
            })
            .AddTo(ref Bag);

        _navigationService.ActiveLeftRailTarget
            .ObserveOnCurrentSynchronizationContext()
            .Subscribe(target =>
            {
                IsDirectMessagesActive = target is LeftRailTarget.DirectMessagesTarget;

                if (target is LeftRailTarget.RelayTarget relayTarget)
                {
                    var match = Relays.FirstOrDefault(r => r.RelayIdentityId == relayTarget.RelayIdentityId);
                    if (!ReferenceEquals(_selectedRelay, match))
                    {
                        _selectedRelay = match;
                        OnPropertyChanged(nameof(SelectedRelay));
                    }
                }
                else
                {
                    if (_selectedRelay != null)
                    {
                        _selectedRelay = null;
                        OnPropertyChanged(nameof(SelectedRelay));
                    }
                }
            })
            .AddTo(ref Bag);

        _currentPersona = _currentPersonaService.CurrentPersona;
        _currentPersonaService.CurrentPersonaChanged
            .ObserveOnCurrentSynchronizationContext()
            .Subscribe(p => CurrentPersona = p)
            .AddTo(ref Bag);
    }

    /// <summary>
    /// Thread-safe synchronized projection of registered relays for UI collection binding ([VM-019]).
    /// </summary>
    public INotifyCollectionChangedSynchronizedViewList<RelayChannelModel> Relays { get; }

    /// <summary>
    /// Live stream of active left rail navigation target.
    /// </summary>
    public ReadOnlyReactiveProperty<LeftRailTarget> ActiveTarget { get; }

    /// <summary>
    /// True when Direct Messages is the active left rail selection.
    /// </summary>
    [ObservableProperty]
    private bool _isDirectMessagesActive = true;

    /// <summary>
    /// Total count of staged inbound requests awaiting review.
    /// </summary>
    [ObservableProperty]
    private int _pendingRequestsCount;

    /// <summary>
    /// True when pending requests exist (drives the Left Rail indicator light).
    /// </summary>
    [ObservableProperty]
    private bool _hasPendingRequests;

    [ObservableProperty]
    private RelayChannelModel? _selectedRelay;

    partial void OnSelectedRelayChanged(RelayChannelModel? value)
    {
        if (value != null)
        {
            _navigationService.NavigateLeftRail(LeftRailTarget.Relay(value.RelayIdentityId));
        }
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCurrentPersona))]
    private ActivePersonaModel? _currentPersona;

    /// <summary>True when a valid persona is currently active.</summary>
    public bool HasCurrentPersona => CurrentPersona != null && CurrentPersona.IdentityId.IsValid;

    /// <summary>
    /// Switches left rail selection to Direct Messages ([NAV-01]).
    /// </summary>
    [RelayCommand]
    public void SelectDirectMessages()
    {
        SelectedRelay = null;
        _navigationService.NavigateLeftRail(LeftRailTarget.DirectMessages);
    }

    /// <summary>
    /// Switches left rail selection to the specified relay channel ([NAV-01]).
    /// </summary>
    [RelayCommand]
    public void SelectRelay(PublicIdentityId relayIdentityId)
    {
        if (!relayIdentityId.IsValid) return;
        var match = Relays.FirstOrDefault(r => r.RelayIdentityId == relayIdentityId);
        SelectedRelay = match;
        _navigationService.NavigateLeftRail(LeftRailTarget.Relay(relayIdentityId));
    }

    /// <summary>
    /// Launches the Add Connection dialog and registers any added relay into the Left Rail ([DESK-05], Flow A).
    /// </summary>
    [RelayCommand]
    public async Task OpenAddConnectionAsync()
    {
        var persona = _currentPersonaService.CurrentPersona;
        if (persona == null || !persona.IdentityId.IsValid) return;

        try
        {
            var result = await _windowManager.ShowDialogAsync<AddConnectionDialogViewModel, PublicIdentityId, AddConnectionDialogResult>(persona.IdentityId);
            if (result != null && result.Status == OutboundConnectionStatus.RelayAdded && !string.IsNullOrWhiteSpace(result.RelayEndpoint))
            {
                _logger.LogInformation("Registering added relay {Endpoint} for persona {PersonaId}", result.RelayEndpoint, persona.IdentityId);
                await _relayStateService.AddRelayAsync(persona.IdentityId, result.RelayEndpoint).ConfigureAwait(true);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to open Add Connection dialog");
        }
    }

    /// <summary>
    /// Launches the Pending Requests review panel ([SYS-CONTACT-CONSENT-GATE]).
    /// </summary>
    [RelayCommand]
    public async Task OpenPendingRequestsAsync()
    {
        var persona = _currentPersonaService.CurrentPersona;
        if (persona == null || !persona.IdentityId.IsValid) return;

        try
        {
            await _windowManager.ShowDialogAsync<PendingRequestsPanelViewModel, PublicIdentityId>(persona.IdentityId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to open Pending Requests panel");
        }
    }

    /// <summary>
    /// Launches the Share My Invitation dialog.
    /// </summary>
    [RelayCommand]
    public async Task OpenShareInvitationAsync()
    {
        var persona = _currentPersonaService.CurrentPersona;
        if (persona == null || !persona.IdentityId.IsValid) return;

        try
        {
            var dialogParams = new ShareInvitationDialogParams(
                IdentityId: persona.IdentityId,
                RoutingToken: BlindedRoutingToken.New(),
                DeliveryToken: DeliveryToken.GenerateRandom(),
                EndpointHosts: new[] { "127.0.0.1" },
                BoundPort: persona.BoundPort);

            await _windowManager.ShowDialogAsync<ShareInvitationDialogViewModel, ShareInvitationDialogParams>(dialogParams);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to open Share Invitation dialog");
        }
    }
}

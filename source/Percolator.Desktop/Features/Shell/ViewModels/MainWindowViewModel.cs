// SPECIFICATION GOVERNANCE: Read Percolator.Desktop/spec/ViewModel.spec.md before modifying this file.
// SPECIFICATION GOVERNANCE: Read Percolator.Desktop/spec/R3.spec.md before modifying this file.

using System;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Percolator.Application.Ports;
using Percolator.Application.Session.Ports;
using Percolator.Desktop.Common.Services;
using Percolator.Desktop.Common.Windowing;
using Percolator.Desktop.Features.Shell.Models;
using Percolator.Desktop.Features.Transports.Models;
using Percolator.Desktop.Features.Transports.ViewModels;
using Percolator.Desktop.ViewModels;
using Percolator.Domain.Delivery.ValueObjects;
using Percolator.Domain.Identities;
using Percolator.Domain.Identities.Model;
using Percolator.Domain.Identities.ValueObjects;
using R3;

namespace Percolator.Desktop.Features.Shell.ViewModels;

/// <summary>
/// ViewModel for the DESK-01 / CONN-11 responsive 3-pane shell frame. Owns transient shell-view state
/// (drawer open/closed, pending-request indicator) and Left Sidebar dialog orchestration.
/// </summary>
public sealed partial class MainWindowViewModel : ViewModelBase
{
    private readonly IWindowManager _windowManager;
    private readonly IPeerContactQueryService _contactQueryService;
    private readonly ICurrentPersonaService _currentPersonaService;

    /// <summary>
    /// Two-way bound to the right-anchored inspector <c>SplitView.IsPaneOpen</c>.
    /// </summary>
    [ObservableProperty]
    private bool _isInspectorOpen;

    /// <summary>
    /// Two-way bound to the right sidebar <c>SplitView.IsPaneOpen</c>. The code-behind
    /// switches the pane between inline and overlay display modes at the 850px breakpoint.
    /// </summary>
    [ObservableProperty]
    private bool _isChatsSidebarOpen = true;

    /// <summary>
    /// Count of staged inbound handshake requests awaiting pull-based review
    /// (drives the Left Sidebar pending-requests indicator light).
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPendingRequests))]
    private int _pendingRequestCount;

    /// <summary>True when staged inbound requests exist; shows the indicator light.</summary>
    public bool HasPendingRequests => PendingRequestCount > 0;

    /// <summary>Header title for the active conversation; empty when none is selected.</summary>
    [ObservableProperty]
    private string _activeConversationTitle = string.Empty;

    /// <summary>
    /// Currently focused persona snapshot driving shell views and dialog orchestration
    /// (INV-DESKTOP-PRESENTATION-MODELS, VM-006).
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCurrentPersona))]
    private ActivePersonaModel? _currentPersona;

    /// <summary>True when a valid persona is currently active in the shell.</summary>
    public bool HasCurrentPersona => CurrentPersona != null && CurrentPersona.IdentityId.IsValid;

    public MainWindowViewModel(
        IWindowManager windowManager,
        IPeerContactQueryService contactQueryService,
        ICurrentPersonaService currentPersonaService)
    {
        _windowManager = windowManager ?? throw new ArgumentNullException(nameof(windowManager));
        _contactQueryService = contactQueryService ?? throw new ArgumentNullException(nameof(contactQueryService));
        _currentPersonaService = currentPersonaService ?? throw new ArgumentNullException(nameof(currentPersonaService));

        _currentPersona = _currentPersonaService.CurrentPersona;

        _currentPersonaService.CurrentPersonaChanged
            .Subscribe(p => CurrentPersona = p)
            .AddTo(ref Bag);
    }

    partial void OnCurrentPersonaChanged(ActivePersonaModel? value)
    {
        _ = RefreshPendingCountAsync();
    }

    [RelayCommand]
    private async Task OpenAddConnectionAsync()
    {
        if (CurrentPersona is not { } persona) return;

        await _windowManager.ShowDialogAsync<AddConnectionDialogViewModel, PublicIdentityId, AddConnectionDialogResult>(persona.IdentityId);
    }

    [RelayCommand]
    private async Task OpenPendingRequestsAsync()
    {
        if (CurrentPersona is not { } persona) return;

        await _windowManager.ShowDialogAsync<PendingRequestsPanelViewModel, PublicIdentityId>(persona.IdentityId);
        await RefreshPendingCountAsync();
    }

    [RelayCommand]
    private async Task OpenShareInvitationAsync()
    {
        if (CurrentPersona is not { } persona) return;

        var dialogParams = new ShareInvitationDialogParams(
            IdentityId: persona.IdentityId,
            RoutingToken: BlindedRoutingToken.New(),
            DeliveryToken: DeliveryToken.GenerateRandom(),
            EndpointHosts: new[] { "127.0.0.1" },
            BoundPort: persona.BoundPort);

        await _windowManager.ShowDialogAsync<ShareInvitationDialogViewModel, ShareInvitationDialogParams>(dialogParams);
    }

    [RelayCommand]
    private void SelectDirectMessages()
    {
        // Selects Direct Messages context in Right Sidebar
    }

    [RelayCommand]
    private void ToggleInspector()
    {
        IsInspectorOpen = !IsInspectorOpen;
    }

    [RelayCommand]
    private void ToggleChatsSidebar()
    {
        IsChatsSidebarOpen = !IsChatsSidebarOpen;
    }

    [RelayCommand]
    public async Task RefreshPendingCountAsync()
    {
        if (CurrentPersona is not { } persona || !persona.IdentityId.IsValid)
        {
            PendingRequestCount = 0;
            return;
        }

        var contacts = await _contactQueryService.GetContactSummariesAsync(persona.IdentityId);
        PendingRequestCount = contacts.Count(c => c.State == ContactState.PendingApproval);
    }
}

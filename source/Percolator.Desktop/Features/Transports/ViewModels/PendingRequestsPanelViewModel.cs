// SPECIFICATION GOVERNANCE: Read Percolator.Desktop/spec/ViewModel.spec.md before modifying this file.

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using ObservableCollections;
using Percolator.Application.Ports;
using Percolator.Application.Profiles;
using Percolator.Desktop.Common.Windowing;
using Percolator.Desktop.Features.Transports.Models;
using Percolator.Desktop.ViewModels;
using Percolator.Domain.Identities;
using Percolator.Domain.Identities.Model;
using R3;

namespace Percolator.Desktop.Features.Transports.ViewModels;

/// <summary>
/// ViewModel managing quarantined inbound unsolicited contact requests and handshake offers ([CONN-09]).
/// Provides silent staging, fingerprint verification previews, and explicit user consent
/// (Accept, Reject, Block) without emitting OS toasts or pings ([INV-UNSOLICITED-CONNECTION-SILENCE]).
/// </summary>
public sealed partial class PendingRequestsPanelViewModel : ViewModelBase
{
    private readonly IDialogContext<PublicIdentityId> _dialogContext;
    private readonly IPeerContactQueryService _contactQueryService;
    private readonly IHandshakeService _handshakeService;
    private readonly IContactRequestCoordinator _contactCoordinator;
    private readonly IPendingHandshakeRepository _pendingHandshakeRepo;
    private readonly ILogger<PendingRequestsPanelViewModel> _logger;

    private readonly ObservableList<InboundPendingRequestModel> _requestsList = new();
    private PublicIdentityId? _activePersonaId;

    public PendingRequestsPanelViewModel(
        IDialogContext<PublicIdentityId> dialogContext,
        IPeerContactQueryService contactQueryService,
        IHandshakeService handshakeService,
        IContactRequestCoordinator contactCoordinator,
        IPendingHandshakeRepository pendingHandshakeRepo,
        ILogger<PendingRequestsPanelViewModel> logger)
    {
        _dialogContext = dialogContext ?? throw new ArgumentNullException(nameof(dialogContext));
        _contactQueryService = contactQueryService ?? throw new ArgumentNullException(nameof(contactQueryService));
        _handshakeService = handshakeService ?? throw new ArgumentNullException(nameof(handshakeService));
        _contactCoordinator = contactCoordinator ?? throw new ArgumentNullException(nameof(contactCoordinator));
        _pendingHandshakeRepo = pendingHandshakeRepo ?? throw new ArgumentNullException(nameof(pendingHandshakeRepo));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        var view = _requestsList.CreateView(r => r);
        Requests = SynchronizationContext.Current != null
            ? view.ToNotifyCollectionChanged(SynchronizationContextCollectionEventDispatcher.Current)
            : view.ToNotifyCollectionChanged();
        view.AddTo(ref Bag);
        Requests.AddTo(ref Bag);

        view.ObserveCountChanged(notifyCurrentCount: true)
            .Subscribe(count => RequestCount = count)
            .AddTo(ref Bag);

        if (dialogContext.Data != default)
        {
            _activePersonaId = dialogContext.Data;
            _ = InitializeAsync(dialogContext.Data);
        }
    }

    /// <summary>
    /// Thread-safe synchronized projection of staged inbound requests for UI data binding ([R3-008]).
    /// </summary>
    public INotifyCollectionChangedSynchronizedViewList<InboundPendingRequestModel> Requests { get; }

    [ObservableProperty]
    private InboundPendingRequestModel? _selectedRequest;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEmpty))]
    private bool _isLoading;

    [ObservableProperty]
    private string? _statusMessage;

    [ObservableProperty]
    private string? _errorMessage;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRequests))]
    [NotifyPropertyChangedFor(nameof(IsEmpty))]
    private int _requestCount;

    /// <summary>True when one or more pending requests are queued.</summary>
    public bool HasRequests => RequestCount > 0;

    /// <summary>True when no pending requests exist and background loading is not active.</summary>
    public bool IsEmpty => RequestCount == 0 && !IsLoading;

    /// <summary>The active persona identity whose incoming contact queue is managed.</summary>
    public PublicIdentityId? ActivePersonaId => _activePersonaId;

    /// <summary>
    /// Initializes the panel with the active persona identity and triggers initial request load.
    /// </summary>
    public async Task InitializeAsync(PublicIdentityId activePersonaId, CancellationToken ct = default)
    {
        _activePersonaId = activePersonaId;
        OnPropertyChanged(nameof(ActivePersonaId));
        await LoadRequestsAsync(ct).ConfigureAwait(true);
    }

    /// <summary>
    /// Refreshes the pending requests list using CQRS read projections ([VM-006]).
    /// </summary>
    [RelayCommand]
    public async Task LoadRequestsAsync(CancellationToken ct = default)
    {
        if (_activePersonaId == null)
        {
            return;
        }

        IsLoading = true;
        ErrorMessage = null;
        try
        {
            var summaries = await _contactQueryService.GetContactSummariesAsync(_activePersonaId.Value, ct).ConfigureAwait(true);
            var pending = summaries.Where(c => c.State == ContactState.PendingApproval).ToList();

            _requestsList.Clear();
            foreach (var item in pending)
            {
                var hex = item.IdentityId.Value.ToString("N");
                var preview = hex.Length >= 8 ? $"{hex[..4]}...{hex[^4..]}" : hex;
                _requestsList.Add(new InboundPendingRequestModel(
                    item.IdentityId,
                    string.IsNullOrWhiteSpace(item.DisplayName) ? $"Peer {preview}" : item.DisplayName,
                    preview,
                    item.AddedAtUtc));
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load inbound pending requests for persona {PersonaId}", _activePersonaId);
            ErrorMessage = "Failed to load pending requests.";
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>
    /// Accepts an inbound request: approves contact, completes cryptographic handshake,
    /// and establishes an active 1-to-1 session.
    /// </summary>
    [RelayCommand]
    public async Task AcceptRequestAsync(InboundPendingRequestModel? request, CancellationToken ct = default)
    {
        if (request == null || _activePersonaId == null) return;

        _logger.LogInformation("Accepting inbound request from peer {PeerId}", request.RequesterId);
        try
        {
            var approveResult = await _contactCoordinator.ApproveRequestAsync(
                _activePersonaId.Value, request.RequesterId, PeerTrustLevel.Tofu, ct).ConfigureAwait(true);

            if (!approveResult.IsSuccess)
            {
                _logger.LogWarning("Failed to approve contact {PeerId}: {Error}", request.RequesterId, approveResult.Error.Description);
                ErrorMessage = approveResult.Error.Description;
                return;
            }

            var completeResult = await _handshakeService.CompletePendingHandshakeAsync(
                _activePersonaId.Value, request.RequesterId, ct).ConfigureAwait(true);

            if (!completeResult.IsSuccess)
            {
                _logger.LogWarning("Failed to complete pending handshake for {PeerId}: {Error}", request.RequesterId, completeResult.Error.Description);
                ErrorMessage = completeResult.Error.Description;
                return;
            }

            _requestsList.Remove(request);
            StatusMessage = $"Accepted connection with {request.DisplayName}.";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Exception while accepting inbound request from {PeerId}", request.RequesterId);
            ErrorMessage = "An unexpected error occurred while accepting request.";
        }
    }

    /// <summary>
    /// Rejects an inbound request: silently purges handshake envelope and marks contact rejected
    /// without sending wire feedback to the initiator.
    /// </summary>
    [RelayCommand]
    public async Task RejectRequestAsync(InboundPendingRequestModel? request, CancellationToken ct = default)
    {
        if (request == null || _activePersonaId == null) return;

        _logger.LogInformation("Rejecting inbound request from peer {PeerId}", request.RequesterId);
        try
        {
            await _pendingHandshakeRepo.DeletePendingHandshakeAsync(
                _activePersonaId.Value, request.RequesterId, ct).ConfigureAwait(true);

            var rejectResult = await _contactCoordinator.RejectRequestAsync(
                _activePersonaId.Value, request.RequesterId, block: false, ct).ConfigureAwait(true);

            if (!rejectResult.IsSuccess)
            {
                _logger.LogWarning("Failed to reject contact {PeerId}: {Error}", request.RequesterId, rejectResult.Error.Description);
            }

            _requestsList.Remove(request);
            StatusMessage = $"Dismissed request from {request.DisplayName}.";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Exception while rejecting inbound request from {PeerId}", request.RequesterId);
            ErrorMessage = "An unexpected error occurred while dismissing request.";
        }
    }

    /// <summary>
    /// Blocks an inbound requester: purges handshake and permanently blacklists the peer ID
    /// per SYS-BLOCK-WIRE-INDISTINGUISHABILITY (subsequent packets silently drop).
    /// </summary>
    [RelayCommand]
    public async Task BlockRequestAsync(InboundPendingRequestModel? request, CancellationToken ct = default)
    {
        if (request == null || _activePersonaId == null) return;

        _logger.LogInformation("Blocking peer {PeerId} per SYS-BLOCK-WIRE-INDISTINGUISHABILITY", request.RequesterId);
        try
        {
            await _pendingHandshakeRepo.DeletePendingHandshakeAsync(
                _activePersonaId.Value, request.RequesterId, ct).ConfigureAwait(true);

            var blockResult = await _contactCoordinator.RejectRequestAsync(
                _activePersonaId.Value, request.RequesterId, block: true, ct).ConfigureAwait(true);

            if (!blockResult.IsSuccess)
            {
                _logger.LogWarning("Failed to block contact {PeerId}: {Error}", request.RequesterId, blockResult.Error.Description);
            }

            _requestsList.Remove(request);
            StatusMessage = $"Blocked {request.DisplayName}. Future packets will be silently dropped.";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Exception while blocking peer {PeerId}", request.RequesterId);
            ErrorMessage = "An unexpected error occurred while blocking peer.";
        }
    }
}

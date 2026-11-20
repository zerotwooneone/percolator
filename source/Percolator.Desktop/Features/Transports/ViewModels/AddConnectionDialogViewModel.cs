// SPECIFICATION GOVERNANCE: Read Percolator.Desktop/spec/ViewModel.spec.md before modifying this file.

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using IOutboundConnectionWorkflow = Percolator.Application.Connections.IOutboundConnectionWorkflow;
using AppOutboundStatus = Percolator.Application.Connections.OutboundConnectionStatus;
using Percolator.Application.Delivery.Ports;
using Percolator.Application.Invitations;
using Percolator.Desktop.Common.Services;
using Percolator.Desktop.Common.Windowing;
using Percolator.Desktop.Features.Chats.Services;
using Percolator.Desktop.Features.Transports.Models;
using Percolator.Desktop.ViewModels;
using Percolator.Domain.Delivery.ValueObjects;
using Percolator.Domain.Identities;
using Percolator.Domain.Identities.ValueObjects;
using Percolator.PluginSdk;

namespace Percolator.Desktop.Features.Transports.ViewModels;

public sealed partial class AddConnectionDialogViewModel : ViewModelBase
{
    private readonly IDialogContext<PublicIdentityId, AddConnectionDialogResult> _dialogContext;
    private readonly IPeerInvitationCodec _invitationCodec;
    private readonly IOutboundConnectionWorkflow _outboundWorkflow;
    private readonly IRelayDirectory _relayDirectory;
    private readonly IRelayMailboxProvisioner _relayProvisioner;
    private readonly IClipboardService _clipboardService;
    private readonly IPersonaDirectory _personaDirectory;
    private readonly IConversationStateService _conversationStateService;
    private readonly ILogger<AddConnectionDialogViewModel> _logger;
    private readonly TimeProvider _timeProvider;

    private PublicIdentityId? _activePersonaId;

    public AddConnectionDialogViewModel(
        IDialogContext<PublicIdentityId, AddConnectionDialogResult> dialogContext,
        IPeerInvitationCodec invitationCodec,
        IOutboundConnectionWorkflow outboundWorkflow,
        IRelayDirectory relayDirectory,
        IRelayMailboxProvisioner relayProvisioner,
        IClipboardService clipboardService,
        IPersonaDirectory personaDirectory,
        IConversationStateService conversationStateService,
        ILogger<AddConnectionDialogViewModel> logger,
        TimeProvider timeProvider)
    {
        _dialogContext = dialogContext ?? throw new ArgumentNullException(nameof(dialogContext));
        _invitationCodec = invitationCodec ?? throw new ArgumentNullException(nameof(invitationCodec));
        _outboundWorkflow = outboundWorkflow ?? throw new ArgumentNullException(nameof(outboundWorkflow));
        _relayDirectory = relayDirectory ?? throw new ArgumentNullException(nameof(relayDirectory));
        _relayProvisioner = relayProvisioner ?? throw new ArgumentNullException(nameof(relayProvisioner));
        _clipboardService = clipboardService ?? throw new ArgumentNullException(nameof(clipboardService));
        _personaDirectory = personaDirectory ?? throw new ArgumentNullException(nameof(personaDirectory));
        _conversationStateService = conversationStateService ?? throw new ArgumentNullException(nameof(conversationStateService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));

        _activePersonaId = dialogContext.Data;
    }


    [ObservableProperty]
    private int _selectedTabIndex;

    [ObservableProperty]
    private string _peerInvitationInput = string.Empty;

    [ObservableProperty]
    private string _relayEndpointInput = string.Empty;

    [ObservableProperty]
    private bool _seedPreKeysToRelay;

    [ObservableProperty]
    private bool _isProbing;

    [ObservableProperty]
    private string _probeStatusText = string.Empty;

    [ObservableProperty]
    private string? _errorMessage;

    public void Initialize(PublicIdentityId activePersonaId)
    {
        _activePersonaId = activePersonaId;
    }

    partial void OnPeerInvitationInputChanged(string value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            var trimmed = value.Trim();
            if (trimmed.StartsWith("percolator://", StringComparison.OrdinalIgnoreCase))
            {
                var stripped = trimmed["percolator://".Length..].Trim();
                if (stripped != value)
                {
                    PeerInvitationInput = stripped;
                }
            }
        }
    }

    partial void OnRelayEndpointInputChanged(string value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            var trimmed = value.Trim();
            if (trimmed.StartsWith("percolator://", StringComparison.OrdinalIgnoreCase))
            {
                var stripped = trimmed["percolator://".Length..].Trim();
                if (stripped != value)
                {
                    RelayEndpointInput = stripped;
                }
            }
        }
    }

    [RelayCommand]
    private async Task PasteClipboardAsync()
    {
        var text = await _clipboardService.GetTextAsync().ConfigureAwait(true);
        if (!string.IsNullOrWhiteSpace(text))
        {
            text = text.Trim();
            if (text.StartsWith("percolator://", StringComparison.OrdinalIgnoreCase))
            {
                text = text["percolator://".Length..].Trim();
            }

            PeerInvitationInput = text;
        }
    }

    [RelayCommand]
    private async Task SubmitPeerConnectionAsync()
    {
        ErrorMessage = null;
        if (string.IsNullOrWhiteSpace(PeerInvitationInput))
        {
            ErrorMessage = "Please enter a peer invitation.";
            return;
        }

        var decodeResult = _invitationCodec.Decode(PeerInvitationInput);
        if (decodeResult.IsFailure)
        {
            ErrorMessage = decodeResult.Error.Description;
            return;
        }

        var payload = decodeResult.Value!;
        IsProbing = true;
        ProbeStatusText = "Probing network route...";

        try
        {
            var workflowResult = await _outboundWorkflow.ConnectAsync(
                payload,
                TimeSpan.FromSeconds(3)).ConfigureAwait(true);

            if (workflowResult.IsFailure)
            {
                ErrorMessage = workflowResult.Error.Description;
                return;
            }

            var peerDisplayName = payload.EndpointHosts.FirstOrDefault(h => !string.IsNullOrWhiteSpace(h))
                ?? $"Peer {payload.IdentityId.Value:N}"[..12];

            OutboundConnectionStatus dialogStatus;
            if (workflowResult.Value!.Status == AppOutboundStatus.Connected)
            {
                dialogStatus = OutboundConnectionStatus.Connected;
                _conversationStateService.RegisterDirectConversation(payload.IdentityId, peerDisplayName);
            }
            else
            {
                dialogStatus = OutboundConnectionStatus.PendingOffline;
                var pendingModel = new OutboundPendingConnectionModel(
                    payload.IdentityId,
                    peerDisplayName,
                    payload,
                    _timeProvider.GetUtcNow());
                _conversationStateService.StagePendingOutbound(pendingModel);
            }

            CloseWithResult(new AddConnectionDialogResult(
                dialogStatus,
                Invitation: payload,
                PeerId: payload.IdentityId));
        }
        finally
        {
            IsProbing = false;
            ProbeStatusText = string.Empty;
        }
    }

    [RelayCommand]
    private async Task SubmitRelayConnectionAsync()
    {
        ErrorMessage = null;
        if (string.IsNullOrWhiteSpace(RelayEndpointInput))
        {
            ErrorMessage = "Please enter a relay endpoint (host:port).";
            return;
        }

        var clean = RelayEndpointInput.Trim();
        if (clean.StartsWith("relay://", StringComparison.OrdinalIgnoreCase) ||
            clean.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
            clean.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
        {
            ErrorMessage = "Invalid relay endpoint format. Expected percolator://host:port or host:port (e.g. 127.0.0.1:5100).";
            return;
        }

        if (clean.StartsWith("percolator://", StringComparison.OrdinalIgnoreCase))
        {
            clean = clean["percolator://".Length..];
        }

        clean = clean.TrimEnd('/');
        var colonIndex = clean.LastIndexOf(':');
        if (colonIndex <= 0 || !int.TryParse(clean[(colonIndex + 1)..], out var port) || port < 1 || port > 65535)
        {
            ErrorMessage = "Invalid relay endpoint format. Expected percolator://host:port or host:port (e.g. 127.0.0.1:5100).";
            return;
        }

        var host = clean[..colonIndex].Trim();
        if (string.IsNullOrWhiteSpace(host))
        {
            ErrorMessage = "Invalid host specified for relay endpoint.";
            return;
        }

        PublicIdentityId activePersonaId;
        if (_activePersonaId.HasValue && _activePersonaId.Value.IsValid)
        {
            activePersonaId = _activePersonaId.Value;
        }
        else
        {
            var personas = await _personaDirectory.ListAsync().ConfigureAwait(true);
            if (personas.Count == 0)
            {
                ErrorMessage = "No active persona found. Please select a persona first.";
                return;
            }

            activePersonaId = personas[0].IdentityId;
        }

        IsProbing = true;
        ProbeStatusText = "Connecting to relay...";

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5), _timeProvider);
            var relayEndpoint = RelayEndpoint.FromHostAndPort(host, port);

            // Deduplicate: If relay endpoint is already known, reuse its identity registration.
            var existingRelays = await _relayDirectory.ListAsync(cts.Token).ConfigureAwait(true);
            var existing = existingRelays.FirstOrDefault(r => r.Endpoint.Equals(relayEndpoint));
            PublicIdentityId relayIdentityId;

            if (existing != null)
            {
                relayIdentityId = existing.RelayIdentityId;
                _logger.LogInformation("Reusing existing relay registration {RelayIdentityId} for endpoint {Endpoint}",
                    relayIdentityId, relayEndpoint);
            }
            else
            {
                relayIdentityId = PublicIdentityId.New();
                var regResult = await _relayDirectory.RegisterRemoteRelayAsync(
                    relayIdentityId,
                    relayEndpoint,
                    pinnedCertificate: null,
                    ct: cts.Token).ConfigureAwait(true);

                if (regResult.IsFailure)
                {
                    _logger.LogWarning("Failed to register remote relay {Endpoint}: {Error}", relayEndpoint, regResult.Error.Description);
                    ErrorMessage = regResult.Error.Description;
                    return;
                }
            }

            var linkResult = await _relayDirectory.LinkIdentityAsync(
                relayIdentityId,
                activePersonaId,
                ct: cts.Token).ConfigureAwait(true);

            if (linkResult.IsFailure)
            {
                _logger.LogWarning("Failed to link persona {PersonaId} to relay {RelayId}: {Error}",
                    activePersonaId, relayIdentityId, linkResult.Error.Description);
                ErrorMessage = linkResult.Error.Description;
                return;
            }

            var provisionResult = await _relayDirectory.EnsureMailboxProvisionedAsync(
                relayIdentityId,
                activePersonaId,
                ct: cts.Token).ConfigureAwait(true);

            if (provisionResult.IsFailure)
            {
                _logger.LogWarning("Failed to provision mailbox on relay {RelayId}: {Error}",
                    relayIdentityId, provisionResult.Error.Description);
                ErrorMessage = provisionResult.Error.Description;
                return;
            }

            if (SeedPreKeysToRelay)
            {
                ProbeStatusText = "Publishing prekeys to relay...";
                var tokensResult = await _relayDirectory.GetMailboxTokensAsync(
                    activePersonaId,
                    relayIdentityId,
                    cts.Token).ConfigureAwait(true);

                if (tokensResult.IsSuccess)
                {
                    var pubResult = await _relayProvisioner.PublishPreKeyBundleAsync(
                        activePersonaId,
                        DeviceId.Primary,
                        relayEndpoint,
                        tokensResult.Value!,
                        newOneTimePreKeyCount: 20,
                        ct: cts.Token).ConfigureAwait(true);

                    if (pubResult.IsFailure)
                    {
                        _logger.LogWarning("Failed to publish prekeys to relay {RelayId}: {Error}",
                            relayIdentityId, pubResult.Error.Description);
                    }
                }
            }

            CloseWithResult(new AddConnectionDialogResult(
                OutboundConnectionStatus.RelayAdded,
                RelayEndpoint: relayEndpoint.ToString()));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to connect and add relay {Host}:{Port}", host, port);
            ErrorMessage = $"Failed to add relay: {ex.Message}";
        }
        finally
        {
            IsProbing = false;
            ProbeStatusText = string.Empty;
        }
    }

    [RelayCommand]
    private void Cancel()
    {
        CloseWithResult(new AddConnectionDialogResult(OutboundConnectionStatus.Cancelled));
    }

    private void CloseWithResult(AddConnectionDialogResult result)
    {
        _dialogContext.Close(result);
    }
}

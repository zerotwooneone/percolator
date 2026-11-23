namespace Percolator.Application.Connections;

using System;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Percolator.Application.Delivery;
using Percolator.Application.Invitations;
using Percolator.Application.Ports;
using Percolator.Domain.Channels.Model;
using Percolator.Domain.Channels.Ports;
using Percolator.Domain.Channels.ValueObjects;
using Percolator.Domain.Common;
using Percolator.Domain.Delivery.Ports;
using Percolator.Domain.Delivery.ValueObjects;
using Percolator.Domain.Identities;
using Percolator.Domain.Identities.Model;
using Percolator.Domain.Identities.Ports;
using Percolator.Domain.Identities.ValueObjects;
using Percolator.Domain.Security.Ports;
using Percolator.PluginSdk;
using Percolator.PluginSdk.Profiles;

public sealed class OutboundConnectionWorkflow : IOutboundConnectionWorkflow
{
    private readonly IRelayDirectory _relayDirectory;
    private readonly IPeerContactRepository _contactRepo;
    private readonly IPreKeyBundleProvider _preKeyProvider;
    private readonly ICryptoEngine _cryptoEngine;
    private readonly IPeerMailboxRoutingRepository _routingRepo;
    private readonly IChannelRepository _channelRepo;
    private readonly IInitialHandshakePayloadProvider _initialPayloadProvider;
    private readonly IHandshakeService _handshakeService;
    private readonly IDateTimeProvider _timeProvider;
    private readonly ILogger<OutboundConnectionWorkflow> _logger;

    public OutboundConnectionWorkflow(
        IRelayDirectory relayDirectory,
        IPeerContactRepository contactRepo,
        IPreKeyBundleProvider preKeyProvider,
        ICryptoEngine cryptoEngine,
        IPeerMailboxRoutingRepository routingRepo,
        IChannelRepository channelRepo,
        IInitialHandshakePayloadProvider initialPayloadProvider,
        IHandshakeService handshakeService,
        IDateTimeProvider timeProvider,
        ILogger<OutboundConnectionWorkflow> logger)
    {
        _relayDirectory = relayDirectory ?? throw new ArgumentNullException(nameof(relayDirectory));
        _contactRepo = contactRepo ?? throw new ArgumentNullException(nameof(contactRepo));
        _preKeyProvider = preKeyProvider ?? throw new ArgumentNullException(nameof(preKeyProvider));
        _cryptoEngine = cryptoEngine ?? throw new ArgumentNullException(nameof(cryptoEngine));
        _routingRepo = routingRepo ?? throw new ArgumentNullException(nameof(routingRepo));
        _channelRepo = channelRepo ?? throw new ArgumentNullException(nameof(channelRepo));
        _initialPayloadProvider = initialPayloadProvider ?? throw new ArgumentNullException(nameof(initialPayloadProvider));
        _handshakeService = handshakeService ?? throw new ArgumentNullException(nameof(handshakeService));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<DomainResult<OutboundConnectionResult>> ConnectAsync(
        PublicIdentityId ownerId,
        PeerInvitationPayload payload,
        TimeSpan probeTimeout,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(payload);

        // 1. Validate payload.ToDomain() -> PeerInvitation
        var invitationResult = payload.ToDomain();
        if (invitationResult.IsFailure)
        {
            return DomainResult<OutboundConnectionResult>.Failure(invitationResult.Error);
        }
        var invitation = invitationResult.Value;

        // 2. Verify ownerId has an active registered mailbox on invitation.RelayEndpoint via IRelayDirectory
        var relays = await _relayDirectory.ListAsync(ct).ConfigureAwait(false);
        var matchingRelay = relays.FirstOrDefault(r => r.Endpoint == invitation.RelayEndpoint);
        if (matchingRelay == null)
        {
            return DomainResult<OutboundConnectionResult>.Failure(new DomainError(
                "EXCHANGE_NOT_LINKED", $"Owner {ownerId} is not a member of Exchange '{invitation.RelayEndpoint.Value}'."));
        }

        var triadResult = await _relayDirectory.GetMailboxTokensAsync(ownerId, matchingRelay.RelayIdentityId, ct).ConfigureAwait(false);
        if (triadResult.IsFailure)
        {
            return DomainResult<OutboundConnectionResult>.Failure(new DomainError(
                "EXCHANGE_NOT_LINKED", $"Owner {ownerId} has no active mailbox linked on Exchange '{invitation.RelayEndpoint.Value}'."));
        }

        // 3. Re-invitation safety check (_contactRepo.GetByPeerIdAsync(ownerId, invitation.InviterIdentityId, ct))
        var existingContact = await _contactRepo.GetByPeerIdAsync(ownerId, invitation.InviterIdentityId, ct).ConfigureAwait(false);
        if (existingContact != null)
        {
            if (existingContact.TrustLevel == PeerTrustLevel.Blocked)
            {
                return DomainResult<OutboundConnectionResult>.Failure(new DomainError(
                    "CONTACT_BLOCKED", $"Peer {invitation.InviterIdentityId} is blocked."));
            }

            if (existingContact.PrimaryPublicKey != null &&
                !CryptographicOperations.FixedTimeEquals(existingContact.PrimaryPublicKey.Span, invitation.InviterIdentityKey.Span))
            {
                return DomainResult<OutboundConnectionResult>.Failure(new DomainError(
                    "IDENTITY_KEY_CHANGED", $"Peer {invitation.InviterIdentityId} IdentityKey does not match previously pinned key."));
            }
        }

        // 4. Fetch consumable PreKeyBundle via IPreKeyBundleProvider.FetchAuthenticatedAsync bounded by probeTimeout
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (probeTimeout > TimeSpan.Zero)
        {
            cts.CancelAfter(probeTimeout);
        }

        DomainResult<PreKeyBundle> bundleResult;
        try
        {
            bundleResult = await _preKeyProvider.FetchAuthenticatedAsync(
                ownerId,
                invitation.RelayEndpoint,
                invitation.RoutingToken,
                DeviceId.Primary,
                cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _logger.LogInformation("FetchAuthenticatedAsync timed out after {Timeout}. Staging as PendingOffline.", probeTimeout);
            return DomainResult<OutboundConnectionResult>.Success(new OutboundConnectionResult(
                OutboundConnectionStatus.PendingOffline,
                invitation.InviterIdentityId,
                payload));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "FetchAuthenticatedAsync failed with network exception. Staging as PendingOffline.");
            return DomainResult<OutboundConnectionResult>.Success(new OutboundConnectionResult(
                OutboundConnectionStatus.PendingOffline,
                invitation.InviterIdentityId,
                payload));
        }

        if (bundleResult.IsFailure)
        {
            if (IsTransientNetworkError(bundleResult.Error))
            {
                _logger.LogInformation("FetchAuthenticatedAsync returned transient error {Error}. Staging as PendingOffline.", bundleResult.Error.Code);
                return DomainResult<OutboundConnectionResult>.Success(new OutboundConnectionResult(
                    OutboundConnectionStatus.PendingOffline,
                    invitation.InviterIdentityId,
                    payload));
            }

            return DomainResult<OutboundConnectionResult>.Failure(bundleResult.Error);
        }

        var fetchedBundle = bundleResult.Value;

        // 5. Verify via invitation.VerifyFetchedBundle(fetchedBundle, _cryptoEngine) — abort immediately on mismatch
        var verifyResult = invitation.VerifyFetchedBundle(fetchedBundle, _cryptoEngine);
        if (verifyResult.IsFailure)
        {
            _logger.LogWarning("PreKeyBundle verification failed against pinned invitation: {Error}", verifyResult.Error.Code);
            return DomainResult<OutboundConnectionResult>.Failure(verifyResult.Error);
        }

        // 6. Persist initial PeerMailboxRoutingCapability (TargetDeliveryToken = null)
        var routingCap = new PeerMailboxRoutingCapability(
            invitation.InviterIdentityId,
            invitation.RoutingToken,
            DeliveryToken.GenerateRandom(),
            invitation.RelayEndpoint,
            _timeProvider.UtcNow);

        await _routingRepo.SaveAsync(routingCap, ct).ConfigureAwait(false);

        // 7. Create or preserve PeerContact (ContactState.PendingOutbound if new)
        if (existingContact == null)
        {
            var createContactResult = PeerContact.CreateOutboundRequest(
                ownerId,
                invitation.InviterIdentityId,
                invitation.InviterIdentityKey,
                _timeProvider);

            if (createContactResult.IsFailure)
            {
                return DomainResult<OutboundConnectionResult>.Failure(createContactResult.Error);
            }

            await _contactRepo.SaveAsync(createContactResult.Value, ct).ConfigureAwait(false);
        }

        // 8. Ensure Exchange-scoped DirectChannel exists
        var channelId = ChannelId.DeterministicForDirect(ownerId, invitation.InviterIdentityId, invitation.RelayEndpoint);
        var existingChannel = await _channelRepo.GetDirectByIdAsync(channelId, ct).ConfigureAwait(false);
        if (existingChannel == null)
        {
            var channelResult = DirectChannel.Create(
                channelId,
                ownerId,
                invitation.InviterIdentityId,
                _timeProvider,
                DirectChannelStatus.PendingAcceptance,
                invitation.RelayEndpoint);

            if (channelResult.IsFailure)
            {
                return DomainResult<OutboundConnectionResult>.Failure(channelResult.Error);
            }

            await _channelRepo.SaveDirectAsync(channelResult.Value, ct).ConfigureAwait(false);
        }

        // 9. Build initial reply capability payload via IInitialHandshakePayloadProvider
        var initialPayloadTuple = await _initialPayloadProvider.CreateInitialPayloadAsync(ownerId, channelId, ct).ConfigureAwait(false);
        AppId? initialAppId = initialPayloadTuple?.AppId;
        ReadOnlyMemory<byte>? initialPayload = initialPayloadTuple?.Payload;

        // 10. Call IHandshakeService.InitiateHandshakeAsync
        var handshakeResult = await _handshakeService.InitiateHandshakeAsync(
            ownerId,
            DeviceId.Primary,
            invitation.InviterIdentityId,
            invitation.RelayEndpoint,
            fetchedBundle,
            initialAppId,
            initialPayload,
            ct).ConfigureAwait(false);

        if (handshakeResult.IsFailure)
        {
            return DomainResult<OutboundConnectionResult>.Failure(handshakeResult.Error);
        }

        // 11. Return Success
        return DomainResult<OutboundConnectionResult>.Success(new OutboundConnectionResult(
            OutboundConnectionStatus.Connected,
            invitation.InviterIdentityId,
            payload));
    }

    private static bool IsTransientNetworkError(DomainError error)
    {
        return error.Code switch
        {
            "PREKEY_BUNDLE_RPC_ERROR" => true,
            "PREKEY_BUNDLE_FETCH_FAILED" => true,
            "PREKEY_BUNDLE_NOT_FOUND" => true,
            "CONNECTION_REFUSED" => true,
            "ENDPOINT_UNREACHABLE" => true,
            "TIMEOUT" => true,
            _ => false
        };
    }
}

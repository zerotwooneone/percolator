using Percolator.Apps.Discovery.Features.NearbyPeerDiscovery;
using Percolator.Apps.Discovery.Features.PrivateRendezvous;
using Percolator.Apps.Discovery.Features.RelayDirectoryHosting;
using Percolator.Domain.Common;
using Percolator.Domain.Identities.ValueObjects;
using Percolator.PluginSdk;

namespace Percolator.Apps.Discovery.Ingress;

/// <summary>
/// Ingress payload dispatcher for Discovery plugin (AppId = 0x02).
/// Enforces session authentication, relay discovery policy, and opcode-based wire dispatch.
/// </summary>
public sealed class DiscoveryPayloadHandler : IAppPayloadHandler
{
    public AppId TargetAppId => AppId.Discovery;

    private readonly IRelayPeerDirectory _directory;
    private readonly IPrivateRendezvousService _rendezvousService;
    private readonly IPayloadSerializer? _serializer;
    private readonly IPayloadSender? _payloadSender;
    private readonly PublicIdentityId _localNodeIdentityId;

    public DiscoveryPayloadHandler(
        IRelayPeerDirectory directory,
        IPrivateRendezvousService rendezvousService,
        IPayloadSerializer? serializer = null,
        IPayloadSender? payloadSender = null,
        PublicIdentityId localNodeIdentityId = default)
    {
        _directory = directory ?? throw new ArgumentNullException(nameof(directory));
        _rendezvousService = rendezvousService ?? throw new ArgumentNullException(nameof(rendezvousService));
        _serializer = serializer;
        _payloadSender = payloadSender;
        _localNodeIdentityId = localNodeIdentityId;
    }

    public async ValueTask<DomainResult> HandleInboundAsync(
        InboundPayloadContext context,
        CancellationToken ct = default)
    {
        if (context.AppId != TargetAppId)
        {
            return DomainResult.Failure(new DomainError(
                "APP_ID_MISMATCH", $"Payload has AppId '{context.AppId}' but handler expects '{TargetAppId}'."));
        }

        if (context.Payload.IsEmpty)
        {
            return DomainResult.Failure(new DomainError(
                "EMPTY_PAYLOAD", "Inbound discovery payload cannot be empty."));
        }

        if (_directory.Policy == RelayDiscoveryPolicy.Disabled)
        {
            return DomainResult.Failure(new DomainError(
                "RELAY_DISCOVERY_DISABLED", "Discovery services are disabled on this relay."));
        }

        DiscoveryOpcode opcode = (DiscoveryOpcode)context.Payload.Span[0];
        ReadOnlyMemory<byte> body = context.Payload.Slice(1);

        if (_serializer is null)
        {
            return DomainResult.Failure(new DomainError(
                "SERIALIZER_NOT_CONFIGURED", "Payload serializer is required to dispatch wire frames."));
        }

        switch (opcode)
        {
            case DiscoveryOpcode.RegisterVisibility:
            {
                var regResult = _serializer.Deserialize<DiscoveryRegistrationPayload>(body);
                if (regResult.IsFailure)
                {
                    return DomainResult.Failure(regResult.Error);
                }

                return await HandleRegistrationAsync(context, regResult.Value, ct).ConfigureAwait(false);
            }

            case DiscoveryOpcode.NearbyQuery:
            {
                var queryResult = _serializer.Deserialize<NearbyDiscoveryQueryPayload>(body);
                if (queryResult.IsFailure)
                {
                    return DomainResult.Failure(queryResult.Error);
                }

                var responseResult = await HandleNearbyQueryAsync(context, queryResult.Value, ct).ConfigureAwait(false);
                if (responseResult.IsFailure)
                {
                    return DomainResult.Failure(responseResult.Error);
                }

                if (_payloadSender is not null)
                {
                    return await SendResponseFrameAsync(
                        context,
                        DiscoveryOpcode.NearbyResponse,
                        responseResult.Value,
                        ct).ConfigureAwait(false);
                }

                return DomainResult.Success();
            }

            case DiscoveryOpcode.RendezvousPublish:
            {
                var publishResult = _serializer.Deserialize<BlindedRendezvousPublishPayload>(body);
                if (publishResult.IsFailure)
                {
                    return DomainResult.Failure(publishResult.Error);
                }

                return await HandleRendezvousPublishAsync(context, publishResult.Value, ct).ConfigureAwait(false);
            }

            case DiscoveryOpcode.RendezvousQuery:
            {
                var queryResult = _serializer.Deserialize<BlindedRendezvousQueryPayload>(body);
                if (queryResult.IsFailure)
                {
                    return DomainResult.Failure(queryResult.Error);
                }

                var responseResult = await HandleRendezvousQueryAsync(context, queryResult.Value, ct).ConfigureAwait(false);
                if (responseResult.IsFailure)
                {
                    return DomainResult.Failure(responseResult.Error);
                }

                if (_payloadSender is not null)
                {
                    return await SendResponseFrameAsync(
                        context,
                        DiscoveryOpcode.RendezvousResponse,
                        responseResult.Value,
                        ct).ConfigureAwait(false);
                }

                return DomainResult.Success();
            }

            default:
                return DomainResult.Failure(new DomainError(
                    "UNKNOWN_DISCOVERY_OPCODE", $"Unsupported discovery opcode 0x{(byte)opcode:X2}."));
        }
    }

    /// <summary>
    /// Processes a registration payload while strictly verifying that the sender is the authenticated session owner.
    /// Anti-impersonation invariant.
    /// </summary>
    public async ValueTask<DomainResult> HandleRegistrationAsync(
        InboundPayloadContext context,
        DiscoveryRegistrationPayload payload,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(payload);

        if (_directory.Policy == RelayDiscoveryPolicy.Disabled)
        {
            return DomainResult.Failure(new DomainError(
                "RELAY_DISCOVERY_DISABLED", "Discovery services are disabled on this relay."));
        }

        // Anti-impersonation: the registration payload's target identity must match the session sender
        if (payload.PeerId != context.SenderIdentityId)
        {
            return DomainResult.Failure(new DomainError(
                "UNAUTHORIZED_REGISTRATION",
                $"Peer '{context.SenderIdentityId}' cannot update discovery visibility for identity '{payload.PeerId}'."));
        }

        var updateResult = await _directory.UpdateVisibilityAsync(payload.PeerId, payload.Visibility, ct).ConfigureAwait(false);
        return updateResult;
    }

    /// <summary>
    /// Processes a query payload on a relay/hosting node and returns candidate descriptors.
    /// Enforces RelayDiscoveryPolicy.Open and anti-impersonation on the requester identity.
    /// </summary>
    public async ValueTask<DomainResult<NearbyDiscoveryResponsePayload>> HandleNearbyQueryAsync(
        InboundPayloadContext context,
        NearbyDiscoveryQueryPayload query,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        if (_directory.Policy == RelayDiscoveryPolicy.Disabled)
        {
            return DomainResult<NearbyDiscoveryResponsePayload>.Failure(new DomainError(
                "RELAY_DISCOVERY_DISABLED", "Discovery services are disabled on this relay."));
        }

        if (_directory.Policy == RelayDiscoveryPolicy.BlindedContactsOnly)
        {
            return DomainResult<NearbyDiscoveryResponsePayload>.Failure(new DomainError(
                "OPEN_DISCOVERY_PROHIBITED",
                "Open nearby discovery queries are prohibited when relay policy is BlindedContactsOnly."));
        }

        if (query.RequesterId != context.SenderIdentityId)
        {
            return DomainResult<NearbyDiscoveryResponsePayload>.Failure(new DomainError(
                "UNAUTHORIZED_QUERY",
                $"Peer '{context.SenderIdentityId}' cannot issue a nearby query on behalf of '{query.RequesterId}'."));
        }

        var limit = query.Limit;
        var discoverable = await _directory.GetDiscoverablePeersAsync(ct).ConfigureAwait(false);

        var descriptors = new List<PeerDiscoveryDescriptor>();
        foreach (var reg in discoverable)
        {
            // Do not return the requester themselves in the candidate list
            if (reg.PeerId == context.SenderIdentityId)
            {
                continue;
            }

            descriptors.Add(new PeerDiscoveryDescriptor(
                reg.PeerId,
                DeliveryRoute.RelayedOneToOne));

            if (descriptors.Count >= limit.Value)
            {
                break;
            }
        }

        return DomainResult<NearbyDiscoveryResponsePayload>.Success(new NearbyDiscoveryResponsePayload(descriptors));
    }

    /// <summary>
    /// Publishes an AEAD-encrypted rendezvous route under a blinded locator on a relay/hosting node.
    /// Permitted when relay policy is Open or BlindedContactsOnly.
    /// </summary>
    public async ValueTask<DomainResult> HandleRendezvousPublishAsync(
        InboundPayloadContext context,
        BlindedRendezvousPublishPayload payload,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(payload);

        if (_directory.Policy == RelayDiscoveryPolicy.Disabled)
        {
            return DomainResult.Failure(new DomainError(
                "RELAY_DISCOVERY_DISABLED", "Discovery services are disabled on this relay."));
        }

        return await _rendezvousService.PublishEncryptedRendezvousAsync(
            payload.Locator,
            payload.EncryptedRoute,
            ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Resolves a batch of blinded locators against the relay's rendezvous table.
    /// Permitted when relay policy is Open or BlindedContactsOnly.
    /// </summary>
    public async ValueTask<DomainResult<BlindedRendezvousResponsePayload>> HandleRendezvousQueryAsync(
        InboundPayloadContext context,
        BlindedRendezvousQueryPayload query,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        if (_directory.Policy == RelayDiscoveryPolicy.Disabled)
        {
            return DomainResult<BlindedRendezvousResponsePayload>.Failure(new DomainError(
                "RELAY_DISCOVERY_DISABLED", "Discovery services are disabled on this relay."));
        }

        var matches = await _rendezvousService.ResolveRendezvousAsync(query.Locators, ct).ConfigureAwait(false);
        return DomainResult<BlindedRendezvousResponsePayload>.Success(new BlindedRendezvousResponsePayload(matches));
    }

    private async ValueTask<DomainResult> SendResponseFrameAsync<T>(
        InboundPayloadContext requestContext,
        DiscoveryOpcode responseOpcode,
        T responsePayload,
        CancellationToken ct)
    {
        ReadOnlyMemory<byte> serializedBody = _serializer!.Serialize(responsePayload);
        byte[] framed = new byte[1 + serializedBody.Length];
        framed[0] = (byte)responseOpcode;
        serializedBody.Span.CopyTo(framed.AsSpan(1));

        var outbound = new OutboundPayloadContext(
            requestContext.ChannelId,
            _localNodeIdentityId,
            requestContext.SenderIdentityId,
            requestContext.SenderDeviceId,
            TargetAppId,
            framed,
            DeliveryRoute.RelayedOneToOne);

        return await _payloadSender!.SendPayloadAsync(outbound, ct).ConfigureAwait(false);
    }
}

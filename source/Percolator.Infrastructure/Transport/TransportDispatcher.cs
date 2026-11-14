using Percolator.Application.Delivery;
using Percolator.Application.Delivery.Ports;
using Percolator.Application.Ports;
using Percolator.PluginSdk.Profiles;

namespace Percolator.Infrastructure.Transport;

using System;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Percolator.Domain.Common;
using Percolator.Domain.Delivery.Hosting;
using Percolator.Domain.Delivery.ValueObjects;
using Percolator.Domain.Identities.ValueObjects;
using Percolator.Domain.Security.Ports;
using Percolator.Domain.Security.ValueObjects;
using Percolator.Infrastructure.Protos;
using Percolator.PluginSdk;

public interface ITransportClientInvoker
{
    Task<DeliveryAckProto> DeliverDirectAsync(Uri endpoint, SealedEnvelopeProto request, CancellationToken ct);
    Task<EnqueueAckProto> EnqueueMailboxAsync(Uri endpoint, SealedEnvelopeProto request, CancellationToken ct);
    Task<DispatchGroupMessageResponse> DispatchGroupMessageAsync(Uri endpoint, DispatchGroupMessageRequest request, CancellationToken ct);
}

public sealed class GrpcTransportClientInvoker : ITransportClientInvoker
{
    private readonly IPeerGrpcChannelFactory _channelFactory;

    public GrpcTransportClientInvoker(IPeerGrpcChannelFactory channelFactory)
    {
        _channelFactory = channelFactory ?? throw new ArgumentNullException(nameof(channelFactory));
    }

    public async Task<DeliveryAckProto> DeliverDirectAsync(Uri endpoint, SealedEnvelopeProto request, CancellationToken ct)
    {
        var channel = _channelFactory.GetOrCreateChannel(endpoint);
        var client = new UnauthenticatedDeliveryService.UnauthenticatedDeliveryServiceClient(channel);
        return await client.DeliverDirectAsync(request, cancellationToken: ct);
    }

    public async Task<EnqueueAckProto> EnqueueMailboxAsync(Uri endpoint, SealedEnvelopeProto request, CancellationToken ct)
    {
        var channel = _channelFactory.GetOrCreateChannel(endpoint);
        var client = new UnauthenticatedDeliveryService.UnauthenticatedDeliveryServiceClient(channel);
        return await client.EnqueueMailboxAsync(request, cancellationToken: ct);
    }

    public async Task<DispatchGroupMessageResponse> DispatchGroupMessageAsync(Uri endpoint, DispatchGroupMessageRequest request, CancellationToken ct)
    {
        var channel = _channelFactory.GetOrCreateChannel(endpoint);
        var client = new AnonymousGroupRelayService.AnonymousGroupRelayServiceClient(channel);
        return await client.DispatchGroupMessageAsync(request, cancellationToken: ct);
    }
}

public sealed class TransportDispatcher : ITransportDispatcher
{
    private readonly ITransportClientInvoker _invoker;
    private readonly IZkProofEngine _zkProofEngine;
    private readonly IGroupCredentialsRepository _groupCredentialsRepository;
    private readonly IPeerMailboxRoutingRepository _mailboxRoutingRepository;
    private readonly IChannelRelayResolver _channelRelayResolver;
    private readonly IStreamRegistryManagement _streamRegistry;

    public TransportDispatcher(
        ITransportClientInvoker invoker,
        IZkProofEngine zkProofEngine,
        IGroupCredentialsRepository groupCredentialsRepository,
        IPeerMailboxRoutingRepository mailboxRoutingRepository,
        IChannelRelayResolver channelRelayResolver,
        IStreamRegistryManagement streamRegistry)
    {
        _invoker = invoker ?? throw new ArgumentNullException(nameof(invoker));
        _zkProofEngine = zkProofEngine ?? throw new ArgumentNullException(nameof(zkProofEngine));
        _groupCredentialsRepository = groupCredentialsRepository ?? throw new ArgumentNullException(nameof(groupCredentialsRepository));
        _mailboxRoutingRepository = mailboxRoutingRepository ?? throw new ArgumentNullException(nameof(mailboxRoutingRepository));
        _channelRelayResolver = channelRelayResolver ?? throw new ArgumentNullException(nameof(channelRelayResolver));
        _streamRegistry = streamRegistry ?? throw new ArgumentNullException(nameof(streamRegistry));
    }

    public static WireFrameKind DetermineWireFrameKind(ReadOnlyMemory<byte> payload, DeliveryRouteType routeType)
    {
        if (routeType == DeliveryRouteType.RelayedGroup)
        {
            return WireFrameKind.GroupBroadcast;
        }

        try
        {
            var handshakeProto = HandshakeInvitationPackageProto.Parser.ParseFrom(payload.Span);
            if (handshakeProto != null &&
                handshakeProto.InitiatorIdentityKey.Length == 32 &&
                handshakeProto.InitiatorEphemeralKey.Length == 32)
            {
                return WireFrameKind.X3dhHandshake;
            }
        }
        catch (InvalidProtocolBufferException)
        {
            // Not a HandshakeInvitationPackageProto
        }

        try
        {
            var groupProto = GroupSessionPackageProto.Parser.ParseFrom(payload.Span);
            if (groupProto != null &&
                groupProto.ConversationId.Length == 16 &&
                !groupProto.Ciphertext.IsEmpty)
            {
                return WireFrameKind.GroupBroadcast;
            }
        }
        catch (InvalidProtocolBufferException)
        {
            // Not a GroupSessionPackageProto
        }

        return WireFrameKind.DirectRatchet;
    }

    private static ReadOnlyMemory<byte> EnsureSealedFrame(OutboxJob job)
    {
        if (job.Payload.Length >= SealedWireFraming.HeaderSize && job.Payload.Span[0] == SealedWireFraming.SealedMagicByte)
        {
            return job.Payload;
        }

        var kind = DetermineWireFrameKind(job.Payload, job.Route.Type);
        byte[] framed = SealedWireFraming.Encode(
            kind,
            job.ChannelId,
            job.OwnerIdentityId,
            DeviceId.Primary,
            job.Payload);

        return framed;
    }

    private static Uri ParseEndpointUri(string endpointStr)
    {
        return endpointStr.Contains("://")
            ? new Uri(endpointStr)
            : new Uri($"https://{endpointStr}");
    }

    public async ValueTask<DomainResult> SendAsync(OutboxJob job, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(job);

        var route = job.Route;
        try
        {
            switch (route.Type)
            {
                case DeliveryRouteType.DirectP2P:
                {
                    var directEndpoint = route.DirectEndpoint ?? (job.RecipientIdentityId.HasValue ? _streamRegistry.GetDirectEndpoint(job.RecipientIdentityId.Value) : null);
                    if (directEndpoint == null)
                    {
                        return DomainResult.Failure(new DomainError("MISSING_DIRECT_ENDPOINT", "Direct endpoint is required for DirectP2P route and could not be resolved from StreamRegistry."));
                    }

                    var framedPayload = EnsureSealedFrame(job);

                    var envelopeProto = new SealedEnvelopeProto
                    {
                        RecipientRoutingToken = route.TargetToken.HasValue
                            ? ByteString.CopyFrom(route.TargetToken.Value.ToByteArray())
                            : ByteString.Empty,
                        DeliveryToken = route.TargetDeliveryToken != null
                            ? ByteString.CopyFrom(route.TargetDeliveryToken.Span)
                            : ByteString.Empty,
                        EncryptedPackage = ByteString.CopyFrom(framedPayload.Span),
                        ExpiresAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow.AddDays(7))
                    };

                    var ack = await _invoker.DeliverDirectAsync(directEndpoint, envelopeProto, ct);
                    if (!ack.Success)
                    {
                        return DomainResult.Failure(new DomainError("DELIVERY_FAILED", ack.ErrorMessage ?? "Direct delivery rejected."));
                    }

                    return DomainResult.Success();
                }

                case DeliveryRouteType.RelayedOneToOne:
                {
                    Uri? relayUri = null;
                    BlindedRoutingToken? targetToken = route.TargetToken;
                    DeliveryToken? deliveryToken = route.TargetDeliveryToken;

                    if (route.RelayEndpoint != null)
                    {
                        relayUri = ParseEndpointUri(route.RelayEndpoint.Value);
                    }
                    else if (job.RecipientIdentityId.HasValue)
                    {
                        var routingInfo = await _mailboxRoutingRepository.GetByPeerIdAsync(job.RecipientIdentityId.Value, ct);
                        if (routingInfo != null && routingInfo.RelayEndpoint != null)
                        {
                            relayUri = ParseEndpointUri(routingInfo.RelayEndpoint.Value);
                            targetToken = routingInfo.RoutingToken;
                            deliveryToken = routingInfo.DeliveryToken;
                        }
                    }

                    if (relayUri == null || !targetToken.HasValue || deliveryToken == null)
                    {
                        return DomainResult.Failure(new DomainError("MISSING_RELAY_ENDPOINT", "Relay endpoint and tokens are required for RelayedOneToOne route."));
                    }

                    var framedPayload = EnsureSealedFrame(job);

                    var envelopeProto = new SealedEnvelopeProto
                    {
                        RecipientRoutingToken = ByteString.CopyFrom(targetToken.Value.ToByteArray()),
                        DeliveryToken = ByteString.CopyFrom(deliveryToken.Span),
                        EncryptedPackage = ByteString.CopyFrom(framedPayload.Span),
                        ExpiresAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow.AddDays(7))
                    };

                    var ack = await _invoker.EnqueueMailboxAsync(relayUri, envelopeProto, ct);
                    if (!ack.Accepted)
                    {
                        return DomainResult.Failure(new DomainError("RELAY_ENQUEUE_REJECTED", "Relay rejected mailbox enqueue."));
                    }

                    return DomainResult.Success();
                }

                case DeliveryRouteType.RelayedGroup:
                {
                    Uri? relayUri = null;
                    if (route.RelayEndpoint != null)
                    {
                        relayUri = ParseEndpointUri(route.RelayEndpoint.Value);
                    }
                    else
                    {
                        relayUri = await _channelRelayResolver.ResolveRelayForChannelAsync(job.ChannelId, ct);
                    }

                    if (relayUri == null)
                    {
                        return DomainResult.Failure(new DomainError("MISSING_RELAY_ENDPOINT", "Relay endpoint is required for RelayedGroup route."));
                    }

                    byte[]? masterKeyBytes = null;
                    byte[]? authMacBytes = null;
                    uint epoch = 0;

                    var creds = await _groupCredentialsRepository.GetAsync(job.ChannelId, ct);
                    if (creds != null)
                    {
                        masterKeyBytes = creds.MasterKey.Span.ToArray();
                        authMacBytes = creds.AuthCredentialMac.Span.ToArray();
                        epoch = creds.CurrentEpoch.Value;
                    }

                    var framedPayload = EnsureSealedFrame(job);

                    byte[] zkProof = Array.Empty<byte>();
                    Span<byte> challengeHash = stackalloc byte[32];
                    RelayGroupLedger.ComputeDispatchChallenge(framedPayload.Span, challengeHash);

                    var proofBytes = _zkProofEngine.GenerateGroupPresentation(
                        epoch,
                        challengeHash,
                        masterKeyBytes ?? ReadOnlySpan<byte>.Empty,
                        authMacBytes ?? ReadOnlySpan<byte>.Empty);
                    zkProof = proofBytes.Span.ToArray();

                    if (masterKeyBytes != null)
                    {
                        CryptographicOperations.ZeroMemory(masterKeyBytes);
                    }

                    var groupRequest = new DispatchGroupMessageRequest
                    {
                        ConversationId = ByteString.CopyFrom(job.ChannelId.ToByteArray()),
                        Epoch = epoch,
                        ZkPresentationProof = ByteString.CopyFrom(zkProof),
                        SenderKeyIteration = 0,
                        Ciphertext = ByteString.CopyFrom(framedPayload.Span),
                        AuthorSignature = ByteString.Empty
                    };

                    var response = await _invoker.DispatchGroupMessageAsync(relayUri, groupRequest, ct);
                    if (!response.Accepted)
                    {
                        return DomainResult.Failure(new DomainError("GROUP_DISPATCH_REJECTED", "Group relay rejected message dispatch."));
                    }

                    return DomainResult.Success();
                }

                default:
                    return DomainResult.Failure(new DomainError("UNSUPPORTED_ROUTE", $"Unsupported route type: {route.Type}"));
            }
        }
        catch (RpcException rpcEx)
        {
            return DomainResult.Failure(new DomainError("RPC_ERROR", rpcEx.Status.Detail ?? rpcEx.Message));
        }
        catch (Exception ex)
        {
            return DomainResult.Failure(new DomainError("TRANSPORT_ERROR", ex.Message));
        }
    }
}

namespace Percolator.Infrastructure.Transport;

using System;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Percolator.Application.Delivery;
using Percolator.Application.Delivery.Ports;
using Percolator.Application.Ports;
using Percolator.Domain.Common;
using Percolator.Domain.Delivery.Hosting;
using Percolator.Domain.Delivery.Model;
using Percolator.Domain.Delivery.Ports;
using Percolator.Domain.Delivery.ValueObjects;
using Percolator.Domain.Identities;
using Percolator.Domain.Identities.Ports;
using Percolator.Domain.Identities.ValueObjects;
using Percolator.Domain.Security.Ports;
using Percolator.Domain.Security.ValueObjects;
using Percolator.Infrastructure.Protos;
using Percolator.PluginSdk;
using Percolator.PluginSdk.Profiles;

public interface ITransportClientInvoker
{
    Task<EnqueueMailboxResponseProto> EnqueueMailboxUnauthenticatedAsync(
        Uri endpoint,
        EnqueueMailboxRequestProto request,
        CancellationToken ct);

    Task<EnqueueMailboxResponseProto> SendInitialHandshakeAuthenticatedAsync(
        PublicIdentityId callerOwnerId,
        Uri endpoint,
        SendInitialHandshakeRequestProto request,
        CancellationToken ct);

    Task<DispatchGroupMessageResponse> DispatchGroupMessageAsync(
        Uri endpoint,
        DispatchGroupMessageRequest request,
        CancellationToken ct);
}

public sealed class GrpcTransportClientInvoker : ITransportClientInvoker
{
    private readonly IPeerGrpcChannelFactory _channelFactory;

    public GrpcTransportClientInvoker(IPeerGrpcChannelFactory channelFactory)
    {
        _channelFactory = channelFactory ?? throw new ArgumentNullException(nameof(channelFactory));
    }

    public async Task<EnqueueMailboxResponseProto> EnqueueMailboxUnauthenticatedAsync(
        Uri endpoint,
        EnqueueMailboxRequestProto request,
        CancellationToken ct)
    {
        var channel = _channelFactory.GetOrCreateChannel(endpoint, ChannelPoolKind.Unauthenticated);
        var client = new UnauthenticatedDeliveryService.UnauthenticatedDeliveryServiceClient(channel);
        return await client.EnqueueMailboxAsync(request, cancellationToken: ct);
    }

    public async Task<EnqueueMailboxResponseProto> SendInitialHandshakeAuthenticatedAsync(
        PublicIdentityId callerOwnerId,
        Uri endpoint,
        SendInitialHandshakeRequestProto request,
        CancellationToken ct)
    {
        var channel = _channelFactory.GetOrCreateChannel(endpoint, ChannelPoolKind.Authenticated);
        var client = new AuthenticatedRelayService.AuthenticatedRelayServiceClient(channel);
        return await client.SendInitialHandshakeAsync(request, cancellationToken: ct);
    }

    public async Task<DispatchGroupMessageResponse> DispatchGroupMessageAsync(
        Uri endpoint,
        DispatchGroupMessageRequest request,
        CancellationToken ct)
    {
        var channel = _channelFactory.GetOrCreateChannel(endpoint, ChannelPoolKind.Unauthenticated);
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
    private readonly IRelayDirectory _relayDirectory;
    private readonly IPeerContactRepository _contactRepository;
    private readonly ILocalIdentityKeyStore _identityKeyStore;

    public TransportDispatcher(
        ITransportClientInvoker invoker,
        IZkProofEngine zkProofEngine,
        IGroupCredentialsRepository groupCredentialsRepository,
        IPeerMailboxRoutingRepository mailboxRoutingRepository,
        IChannelRelayResolver channelRelayResolver,
        IRelayDirectory relayDirectory,
        IPeerContactRepository contactRepository,
        ILocalIdentityKeyStore identityKeyStore)
    {
        _invoker = invoker ?? throw new ArgumentNullException(nameof(invoker));
        _zkProofEngine = zkProofEngine ?? throw new ArgumentNullException(nameof(zkProofEngine));
        _groupCredentialsRepository = groupCredentialsRepository ?? throw new ArgumentNullException(nameof(groupCredentialsRepository));
        _mailboxRoutingRepository = mailboxRoutingRepository ?? throw new ArgumentNullException(nameof(mailboxRoutingRepository));
        _channelRelayResolver = channelRelayResolver ?? throw new ArgumentNullException(nameof(channelRelayResolver));
        _relayDirectory = relayDirectory ?? throw new ArgumentNullException(nameof(relayDirectory));
        _contactRepository = contactRepository ?? throw new ArgumentNullException(nameof(contactRepository));
        _identityKeyStore = identityKeyStore ?? throw new ArgumentNullException(nameof(identityKeyStore));
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

    private async Task<ReadOnlyMemory<byte>> EnsureSealedFrameAsync(OutboxJob job, CancellationToken ct)
    {
        if (job.Payload.Length >= 2 && job.Payload.Span[0] == SealedWireFraming.SealedMagicByte &&
            (job.Payload.Span[1] == SealedWireFraming.Version1 || job.Payload.Span[1] == SealedWireFraming.VersionGroupBroadcast))
        {
            return job.Payload;
        }

        var kind = DetermineWireFrameKind(job.Payload, job.Route.Type);

        if (kind == WireFrameKind.GroupBroadcast)
        {
            var senderPub = await _identityKeyStore.GetIdentityPublicKeyAsync(job.OwnerIdentityId, ct).ConfigureAwait(false);
            if (senderPub != null)
            {
                return SealedWireFraming.EncodeGroupBroadcast(
                    job.ChannelId,
                    job.OwnerIdentityId,
                    DeviceId.Primary,
                    senderPub,
                    job.Payload);
            }
            return job.Payload;
        }

        IdentityKey? recipientKey = null;
        if (job.RecipientIdentityId.HasValue)
        {
            var contact = await _contactRepository.GetByPeerIdAsync(job.OwnerIdentityId, job.RecipientIdentityId.Value, ct).ConfigureAwait(false);
            if (contact != null)
            {
                recipientKey = contact.PrimaryPublicKey;
            }
        }

        var senderPriv = await _identityKeyStore.GetIdentityPrivateKeyAsync(job.OwnerIdentityId, ct).ConfigureAwait(false);
        var senderPublicKey = await _identityKeyStore.GetIdentityPublicKeyAsync(job.OwnerIdentityId, ct).ConfigureAwait(false);

        if (recipientKey == null || senderPriv == null || senderPublicKey == null)
        {
            return job.Payload;
        }

        try
        {
            byte[] framed = SealedWireFraming.Encode(
                kind,
                job.ChannelId,
                job.OwnerIdentityId,
                DeviceId.Primary,
                senderPriv.Span,
                senderPublicKey,
                recipientKey,
                job.Payload);

            return framed;
        }
        finally
        {
            senderPriv.Dispose();
        }
    }

    private async Task<MailboxTokenTriad> GetCallerMailboxTokensAsync(
        PublicIdentityId ownerId,
        RelayEndpoint relayEndpoint,
        CancellationToken ct)
    {
        var entries = await _relayDirectory.ListAsync(ct).ConfigureAwait(false);
        var entry = entries.FirstOrDefault(e => e.Endpoint == relayEndpoint && e.LinkedOwnerIdentityIds.Contains(ownerId));
        if (entry != null)
        {
            var tokens = await _relayDirectory.GetMailboxTokensAsync(ownerId, entry.RelayIdentityId, ct).ConfigureAwait(false);
            if (tokens.IsSuccess)
            {
                return tokens.Value;
            }
        }

        var d1 = new byte[32];
        var d2 = new byte[32];
        RandomNumberGenerator.Fill(d1);
        RandomNumberGenerator.Fill(d2);
        return new MailboxTokenTriad(BlindedRoutingToken.New(), DeliveryToken.FromSpan(d1), DeliveryToken.FromSpan(d2));
    }

    public async ValueTask<DomainResult> SendAsync(OutboxJob job, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(job);

        var route = job.Route;
        try
        {
            switch (route.Type)
            {
                case DeliveryRouteType.RelayedOneToOne when route.TargetDeliveryToken is null:
                {
                    var framedPayload = await EnsureSealedFrameAsync(job, ct).ConfigureAwait(false);
                    var envelopeProto = new SealedEnvelopeProto
                    {
                        RecipientRoutingToken = ByteString.CopyFrom(route.TargetToken.ToByteArray()),
                        EncryptedPackage = ByteString.CopyFrom(framedPayload.Span),
                        ExpiresAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow.AddDays(7))
                    };

                    var callerTokens = await GetCallerMailboxTokensAsync(job.OwnerIdentityId, route.RelayEndpoint, ct).ConfigureAwait(false);
                    var initialRequest = new SendInitialHandshakeRequestProto
                    {
                        RoutingToken = ByteString.CopyFrom(callerTokens.RoutingToken.ToByteArray()),
                        DrainToken = ByteString.CopyFrom(callerTokens.DrainToken.Span),
                        RecipientRoutingToken = ByteString.CopyFrom(route.TargetToken.ToByteArray()),
                        Envelope = envelopeProto
                    };

                    var response = await _invoker.SendInitialHandshakeAuthenticatedAsync(
                        job.OwnerIdentityId,
                        route.RelayEndpoint.ToUri(),
                        initialRequest,
                        ct).ConfigureAwait(false);

                    if (!response.Success)
                    {
                        return DomainResult.Failure(new DomainError("INITIAL_HANDSHAKE_REJECTED", "Relay rejected initial handshake message."));
                    }

                    return DomainResult.Success();
                }

                case DeliveryRouteType.RelayedOneToOne:
                {
                    var framedPayload = await EnsureSealedFrameAsync(job, ct).ConfigureAwait(false);
                    var envelopeProto = new SealedEnvelopeProto
                    {
                        RecipientRoutingToken = ByteString.CopyFrom(route.TargetToken.ToByteArray()),
                        DeliveryToken = ByteString.CopyFrom(route.TargetDeliveryToken!.Span),
                        EncryptedPackage = ByteString.CopyFrom(framedPayload.Span),
                        ExpiresAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow.AddDays(7))
                    };

                    var request = new EnqueueMailboxRequestProto
                    {
                        RecipientRoutingToken = ByteString.CopyFrom(route.TargetToken.ToByteArray()),
                        DeliveryToken = ByteString.CopyFrom(route.TargetDeliveryToken!.Span),
                        Envelope = envelopeProto
                    };

                    var response = await _invoker.EnqueueMailboxUnauthenticatedAsync(
                        route.RelayEndpoint.ToUri(),
                        request,
                        ct).ConfigureAwait(false);

                    if (!response.Success)
                    {
                        return DomainResult.Failure(new DomainError("RELAY_ENQUEUE_REJECTED", "Relay rejected mailbox enqueue."));
                    }

                    return DomainResult.Success();
                }

                case DeliveryRouteType.RelayedGroup:
                {
                    var framedPayload = await EnsureSealedFrameAsync(job, ct).ConfigureAwait(false);

                    if (route.TargetDeliveryToken != null)
                    {
                        var envelopeProto = new SealedEnvelopeProto
                        {
                            RecipientRoutingToken = ByteString.CopyFrom(route.TargetToken.ToByteArray()),
                            DeliveryToken = ByteString.CopyFrom(route.TargetDeliveryToken.Span),
                            EncryptedPackage = ByteString.CopyFrom(framedPayload.Span),
                            ExpiresAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow.AddDays(7))
                        };

                        var request = new EnqueueMailboxRequestProto
                        {
                            RecipientRoutingToken = ByteString.CopyFrom(route.TargetToken.ToByteArray()),
                            DeliveryToken = ByteString.CopyFrom(route.TargetDeliveryToken.Span),
                            Envelope = envelopeProto
                        };

                        var response = await _invoker.EnqueueMailboxUnauthenticatedAsync(
                            route.RelayEndpoint.ToUri(),
                            request,
                            ct).ConfigureAwait(false);

                        if (!response.Success)
                        {
                            return DomainResult.Failure(new DomainError("RELAY_ENQUEUE_REJECTED", "Relay rejected mailbox enqueue."));
                        }

                        return DomainResult.Success();
                    }

                    Uri? relayUri = route.RelayEndpoint != null
                        ? route.RelayEndpoint.ToUri()
                        : await _channelRelayResolver.ResolveRelayForChannelAsync(job.ChannelId, ct).ConfigureAwait(false);

                    if (relayUri == null)
                    {
                        return DomainResult.Failure(new DomainError("MISSING_RELAY_ENDPOINT", "Relay endpoint is required for RelayedGroup route."));
                    }

                    byte[]? masterKeyBytes = null;
                    byte[]? authMacBytes = null;
                    uint epoch = 0;

                    var creds = await _groupCredentialsRepository.GetAsync(job.ChannelId, ct).ConfigureAwait(false);
                    if (creds != null)
                    {
                        masterKeyBytes = creds.MasterKey.Span.ToArray();
                        authMacBytes = creds.AuthCredentialMac.Span.ToArray();
                        epoch = creds.CurrentEpoch.Value;
                    }

                    byte[] zkProof;
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

                    var groupResponse = await _invoker.DispatchGroupMessageAsync(relayUri, groupRequest, ct).ConfigureAwait(false);
                    if (!groupResponse.Accepted)
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

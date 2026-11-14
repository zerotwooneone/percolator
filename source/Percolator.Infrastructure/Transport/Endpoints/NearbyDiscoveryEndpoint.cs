namespace Percolator.Infrastructure.Transport.Endpoints;

using System;
using System.Security.Cryptography;
using System.Threading.Tasks;
using Google.Protobuf;
using Grpc.Core;
using Percolator.Apps.Discovery.Features.RelayDirectoryHosting;
using Percolator.Domain.Common;
using Percolator.Domain.Delivery.Hosting;
using Percolator.Domain.Delivery.Ports;
using Percolator.Domain.Delivery.ValueObjects;
using Percolator.Domain.Identities.ValueObjects;
using Percolator.Domain.Security.Ports;
using Percolator.Infrastructure.Protos;
using Percolator.Infrastructure.Transport;
using Percolator.PluginSdk;

public sealed class NearbyDiscoveryEndpoint : NearbyDiscoveryService.NearbyDiscoveryServiceBase
{
    private readonly IRelayPeerDirectory _peerDirectory;
    private readonly IRelayMailboxCapabilityStore _capabilityStore;
    private readonly IRelayPreKeyRepository _preKeyRepository;
    private readonly IDateTimeProvider _timeProvider;
    private readonly int _boundPort;
    private readonly ICryptoEngine _cryptoEngine;

    public NearbyDiscoveryEndpoint(
        IRelayPeerDirectory peerDirectory,
        IRelayMailboxCapabilityStore capabilityStore,
        IRelayPreKeyRepository preKeyRepository,
        IDateTimeProvider timeProvider,
        int boundPort,
        ICryptoEngine cryptoEngine)
    {
        _peerDirectory = peerDirectory ?? throw new ArgumentNullException(nameof(peerDirectory));
        _capabilityStore = capabilityStore ?? throw new ArgumentNullException(nameof(capabilityStore));
        _preKeyRepository = preKeyRepository ?? throw new ArgumentNullException(nameof(preKeyRepository));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _boundPort = boundPort;
        _cryptoEngine = cryptoEngine ?? throw new ArgumentNullException(nameof(cryptoEngine));
    }

    public static byte[] BuildVisibilitySignaturePayload(
        Domain.Identities.PublicIdentityId peerId,
        int visibility,
        long timestampUnixMs,
        ReadOnlySpan<byte> routingToken = default,
        ReadOnlySpan<byte> deliveryToken = default)
        => DiscoveryWireFraming.BuildVisibilitySignaturePayload(peerId, visibility, timestampUnixMs, routingToken, deliveryToken);

    public override async Task<NearbyDiscoveryResponseProto> QueryNearby(
        NearbyDiscoveryQueryProto request,
        ServerCallContext context)
    {
        int limit = request.RequestedLimit > 0 ? request.RequestedLimit : 10;
        var peers = await _peerDirectory.GetDiscoverablePeersAsync(limit, context.CancellationToken);

        Domain.Identities.PublicIdentityId? requesterId = null;
        if (request.RequesterId != null && (request.RequesterId.Length == 16 || request.RequesterId.Length == 32))
        {
            try
            {
                requesterId = Domain.Identities.PublicIdentityId.FromBytes(request.RequesterId.Span);
            }
            catch { }
        }

        var response = new NearbyDiscoveryResponseProto();

        foreach (var reg in peers)
        {
            if (requesterId.HasValue && reg.PeerId == requesterId.Value)
            {
                continue;
            }

            response.DiscoverablePeerIds.Add(ByteString.CopyFrom(reg.PeerId.ToByteArray()));

            var descriptor = new PeerDiscoveryDescriptorProto
            {
                PeerId = ByteString.CopyFrom(reg.PeerId.ToByteArray()),
                Route = (int)DeliveryRoute.RelayedOneToOne,
                RelayEndpoint = $"https://127.0.0.1:{_boundPort}"
            };

            // Tokens advertised at registration — the directory record is the single
            // source of truth; no owner-keyed capability lookup exists anymore.
            if (reg.RoutingToken.HasValue && (reg.DeliveryToken!=null))
            {
                descriptor.RoutingToken = ByteString.CopyFrom(reg.RoutingToken.Value.ToByteArray());
                descriptor.DeliveryToken = ByteString.CopyFrom(reg.DeliveryToken.Span);
            }

            response.Descriptors.Add(descriptor);
        }

        return response;
    }

    public override async Task<DiscoveryRegistrationProto> RegisterVisibility(
        DiscoveryRegistrationProto request,
        ServerCallContext context)
    {
        if (request.PeerId.Length != 16 && request.PeerId.Length != 32)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "Peer ID must be 16 or 32 bytes."));
        }

        var peerId = Domain.Identities.PublicIdentityId.FromBytes(request.PeerId.Span);

        if (request.IdentityKey.Length != 32 || request.Signature.Length != 64)
        {
            throw new RpcException(new Status(StatusCode.PermissionDenied, "Invalid identity key or signature length."));
        }

        if (request.TimestampUnixMs <= 0)
        {
            throw new RpcException(new Status(StatusCode.PermissionDenied, "Registration timestamp must be positive."));
        }

        DateTimeOffset registrationTime;
        try
        {
            registrationTime = DateTimeOffset.FromUnixTimeMilliseconds(request.TimestampUnixMs);
        }
        catch (ArgumentOutOfRangeException)
        {
            throw new RpcException(new Status(StatusCode.PermissionDenied, "Registration timestamp is outside the valid range."));
        }

        if (Math.Abs((_timeProvider.UtcNow - registrationTime).TotalMinutes) > 5)
        {
            throw new RpcException(new Status(StatusCode.PermissionDenied, "Registration timestamp is outside the acceptable drift window."));
        }

        var payload = DiscoveryWireFraming.BuildVisibilitySignaturePayload(
            peerId,
            request.Visibility,
            request.TimestampUnixMs,
            request.RoutingToken.Span,
            request.DeliveryToken.Span);
        var identityKey = IdentityKey.FromSpan(request.IdentityKey.Span);
        if (!_cryptoEngine.VerifyEd25519Signature(identityKey, payload, request.Signature.Span))
        {
            throw new RpcException(new Status(StatusCode.PermissionDenied, "Invalid visibility registration signature."));
        }

        BlindedRoutingToken? routingToken = null;
        DeliveryToken? deliveryToken = null;
        if (request.RoutingToken.Length == 16 && request.DeliveryToken.Length == 32)
        {
            routingToken = BlindedRoutingToken.FromBytes(request.RoutingToken.ToByteArray());
            deliveryToken = DeliveryToken.FromSpan(request.DeliveryToken.Span);
        }

        // Pre-key availability is checked against the advertised mailbox routing token.
        // The identity-key pin still authenticates the published bundle's key — the
        // directory just no longer keys that lookup by the peer's public identity.
        IReadOnlyList<RelayHostedPreKeyBundle> bundles =
            routingToken.HasValue
                ? await _preKeyRepository.GetAllForMailboxAsync(routingToken.Value, context.CancellationToken)
                : Array.Empty<RelayHostedPreKeyBundle>();
        if (bundles.Count > 0)
        {
            if (!CryptographicOperations.FixedTimeEquals(bundles[0].IdentityKey.Span, request.IdentityKey.Span))
            {
                throw new RpcException(new Status(StatusCode.PermissionDenied, "Identity key does not match pre-key bundle identity."));
            }
        }

        var visibility = (PeerDiscoveryVisibility)request.Visibility;
        var nowUtc = _timeProvider.UtcNow;

        var existing = await _peerDirectory.GetPeerAsync(peerId, context.CancellationToken);
        if (existing != null)
        {
            var updateResult = await _peerDirectory.UpdateVisibilityAsync(
                peerId,
                visibility,
                routingToken,
                deliveryToken,
                context.CancellationToken);
            if (!updateResult.IsSuccess)
            {
                throw new RpcException(new Status(StatusCode.Internal, updateResult.Error.Description));
            }
        }
        else
        {
            var hasPreKey = bundles.Count > 0;
            var registration = new RelayPeerRegistration(
                PeerId: peerId,
                Visibility: visibility,
                HasAuthenticatedHandshake: true,
                HasActiveSignedPreKey: hasPreKey,
                RegisteredAtUtc: nowUtc,
                LastUpdatedAtUtc: nowUtc)
            {
                RoutingToken = routingToken,
                DeliveryToken = deliveryToken
            };

            var regResult = await _peerDirectory.RegisterPeerAsync(registration, context.CancellationToken);
            if (!regResult.IsSuccess)
            {
                throw new RpcException(new Status(StatusCode.Internal, regResult.Error.Description));
            }
        }

        return request;
    }
}

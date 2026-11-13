namespace Percolator.Infrastructure.Tests.Transport.Endpoints;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Google.Protobuf;
using Grpc.Core;
using NUnit.Framework;
using Percolator.Apps.Discovery.Features.RelayDirectoryHosting;
using Percolator.Apps.Discovery.Ports;
using Percolator.Domain.Common;
using Percolator.Domain.Delivery.Hosting;
using Percolator.Domain.Delivery.Ports;
using Percolator.Domain.Delivery.ValueObjects;
using Percolator.Domain.Identities.ValueObjects;
using Percolator.Domain.Security.Ports;
using Percolator.Domain.Security.ValueObjects;
using Percolator.Infrastructure.Protos;
using Percolator.Infrastructure.Security;
using Percolator.Infrastructure.Tests.Fakes;
using Percolator.Infrastructure.Transport;
using Percolator.Infrastructure.Transport.Endpoints;

[TestFixture]
public sealed class NearbyDiscoveryEndpointTests
{
    private sealed class InMemoryPreKeyRepo : IRelayPreKeyRepository
    {
        private readonly ConcurrentDictionary<(BlindedRoutingToken, DeviceId), RelayHostedPreKeyBundle> _bundles = new();

        public Task<RelayHostedPreKeyBundle?> GetAsync(BlindedRoutingToken routingToken, DeviceId deviceId, CancellationToken cancellationToken = default)
        {
            _bundles.TryGetValue((routingToken, deviceId), out var bundle);
            return Task.FromResult(bundle);
        }

        public Task<IReadOnlyList<RelayHostedPreKeyBundle>> GetAllForMailboxAsync(BlindedRoutingToken routingToken, CancellationToken cancellationToken = default)
        {
            IReadOnlyList<RelayHostedPreKeyBundle> list = _bundles.Values.Where(b => b.RoutingToken == routingToken).ToList();
            return Task.FromResult(list);
        }

        public Task<bool> HasActivePreKeyAsync(BlindedRoutingToken routingToken, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(_bundles.Values.Any(b => b.RoutingToken == routingToken));
        }

        public Task SaveAsync(RelayHostedPreKeyBundle bundle, CancellationToken cancellationToken = default)
        {
            _bundles[(bundle.RoutingToken, bundle.DeviceId)] = bundle;
            return Task.CompletedTask;
        }

        public Task DeleteAsync(BlindedRoutingToken routingToken, DeviceId deviceId, CancellationToken cancellationToken = default)
        {
            _bundles.TryRemove((routingToken, deviceId), out _);
            return Task.CompletedTask;
        }
    }

    private sealed class FixedTimeProvider : IDateTimeProvider
    {
        public DateTimeOffset UtcNow { get; set; } = DateTimeOffset.UtcNow;
    }

    [Test]
    public void Constructor_NullCryptoEngine_ThrowsArgumentNullException()
    {
        var timeProvider = new FixedTimeProvider();
        var peerDir = new InMemoryRelayPeerDirectory(new DiscoveryAdmissionGate(), RelayDiscoveryPolicy.Open);
        var capabilityStore = new FakeRelayMailboxCapabilityStore();
        var preKeyRepo = new InMemoryPreKeyRepo();

        var act = () => new NearbyDiscoveryEndpoint(peerDir, capabilityStore, preKeyRepo, timeProvider, 20600, null!);
        act.Should().Throw<ArgumentNullException>().WithParameterName("cryptoEngine");
    }

    [Test]
    public async Task RegisterVisibility_WithValidSignature_RegistersSuccessfully()
    {
        var crypto = new SignalCryptoEngine(new CryptographyOptions());
        var timeProvider = new FixedTimeProvider();
        var peerDir = new InMemoryRelayPeerDirectory(new DiscoveryAdmissionGate(), RelayDiscoveryPolicy.Open);
        var capabilityStore = new FakeRelayMailboxCapabilityStore();
        var preKeyRepo = new InMemoryPreKeyRepo();
        var endpoint = new NearbyDiscoveryEndpoint(peerDir, capabilityStore, preKeyRepo, timeProvider, 20600, crypto);

        var peerId = PublicIdentityId.New();
        var (privKey, pubKey) = crypto.GenerateEphemeralKeyPair();
        var routingToken = BlindedRoutingToken.New();
        var deliveryTokenBytes = RandomNumberGenerator.GetBytes(32);

        var bundle = RelayHostedPreKeyBundle.Rehydrate(
            routingToken,
            DeviceId.Primary,
            IdentityKey.FromSpan(pubKey.Span),
            DhPublicKey.FromSpan(new byte[32]),
            DeviceLinkProof.FromSpan(new byte[64]));
        await preKeyRepo.SaveAsync(bundle);

        int visibility = (int)PeerDiscoveryVisibility.Discoverable;
        long timestamp = timeProvider.UtcNow.ToUnixTimeMilliseconds();

        var payload = DiscoveryWireFraming.BuildVisibilitySignaturePayload(
            peerId, visibility, timestamp, routingToken.ToByteArray(), deliveryTokenBytes);
        var signature = crypto.SignEd25519(privKey.Span, payload);

        var request = new DiscoveryRegistrationProto
        {
            PeerId = ByteString.CopyFrom(peerId.ToByteArray()),
            Visibility = visibility,
            TimestampUnixMs = timestamp,
            IdentityKey = ByteString.CopyFrom(pubKey.Span),
            Signature = ByteString.CopyFrom(signature),
            RoutingToken = ByteString.CopyFrom(routingToken.ToByteArray()),
            DeliveryToken = ByteString.CopyFrom(deliveryTokenBytes)
        };

        var response = await endpoint.RegisterVisibility(request, new TestServerCallContext());
        response.Should().NotBeNull();
        response.PeerId.Should().Equal(request.PeerId);

        var query = new NearbyDiscoveryQueryProto { RequestedLimit = 10 };
        var queryResp = await endpoint.QueryNearby(query, new TestServerCallContext());
        queryResp.DiscoverablePeerIds.Should().Contain(ByteString.CopyFrom(peerId.ToByteArray()));
    }

    [Test]
    public async Task RegisterVisibility_WithoutPreKeys_Succeeds_ButNotDiscoverableUntilPreKeysPublished()
    {
        var crypto = new SignalCryptoEngine(new CryptographyOptions());
        var timeProvider = new FixedTimeProvider();
        var peerDir = new InMemoryRelayPeerDirectory(new DiscoveryAdmissionGate(), RelayDiscoveryPolicy.Open);
        var capabilityStore = new FakeRelayMailboxCapabilityStore();
        var preKeyRepo = new InMemoryPreKeyRepo();
        var endpoint = new NearbyDiscoveryEndpoint(peerDir, capabilityStore, preKeyRepo, timeProvider, 20600, crypto);

        var peerId = PublicIdentityId.New();
        var (privKey, pubKey) = crypto.GenerateEphemeralKeyPair();
        var routingToken = BlindedRoutingToken.New();
        var deliveryTokenBytes = RandomNumberGenerator.GetBytes(32);
        int visibility = (int)PeerDiscoveryVisibility.Discoverable;
        long timestamp = timeProvider.UtcNow.ToUnixTimeMilliseconds();

        var payload = DiscoveryWireFraming.BuildVisibilitySignaturePayload(
            peerId, visibility, timestamp, routingToken.ToByteArray(), deliveryTokenBytes);
        var signature = crypto.SignEd25519(privKey.Span, payload);

        var request = new DiscoveryRegistrationProto
        {
            PeerId = ByteString.CopyFrom(peerId.ToByteArray()),
            Visibility = visibility,
            TimestampUnixMs = timestamp,
            IdentityKey = ByteString.CopyFrom(pubKey.Span),
            Signature = ByteString.CopyFrom(signature),
            RoutingToken = ByteString.CopyFrom(routingToken.ToByteArray()),
            DeliveryToken = ByteString.CopyFrom(deliveryTokenBytes)
        };

        var response = await endpoint.RegisterVisibility(request, new TestServerCallContext());
        response.Should().NotBeNull();

        // Not discoverable yet because no active signed prekey is published
        var query = new NearbyDiscoveryQueryProto { RequestedLimit = 10 };
        var queryResp = await endpoint.QueryNearby(query, new TestServerCallContext());
        queryResp.DiscoverablePeerIds.Should().NotContain(ByteString.CopyFrom(peerId.ToByteArray()));
    }

    [Test]
    public async Task RegisterVisibility_TimestampOutOfRange_ThrowsPermissionDenied()
    {
        var crypto = new SignalCryptoEngine(new CryptographyOptions());
        var timeProvider = new FixedTimeProvider();
        var peerDir = new InMemoryRelayPeerDirectory(new DiscoveryAdmissionGate(), RelayDiscoveryPolicy.Open);
        var capabilityStore = new FakeRelayMailboxCapabilityStore();
        var preKeyRepo = new InMemoryPreKeyRepo();
        var endpoint = new NearbyDiscoveryEndpoint(peerDir, capabilityStore, preKeyRepo, timeProvider, 20600, crypto);

        var peerId = PublicIdentityId.New();
        var (privKey, pubKey) = crypto.GenerateEphemeralKeyPair();

        var request = new DiscoveryRegistrationProto
        {
            PeerId = ByteString.CopyFrom(peerId.ToByteArray()),
            Visibility = (int)PeerDiscoveryVisibility.Discoverable,
            TimestampUnixMs = long.MaxValue,
            IdentityKey = ByteString.CopyFrom(pubKey.Span),
            Signature = ByteString.CopyFrom(new byte[64])
        };

        var act = async () => await endpoint.RegisterVisibility(request, new TestServerCallContext());
        await act.Should().ThrowAsync<RpcException>().Where(e => e.StatusCode == StatusCode.PermissionDenied);
    }

    [Test]
    public async Task RegisterVisibility_WithDriftedTimestamp_ThrowsPermissionDenied()
    {
        var crypto = new SignalCryptoEngine(new CryptographyOptions());
        var timeProvider = new FixedTimeProvider();
        var peerDir = new InMemoryRelayPeerDirectory(new DiscoveryAdmissionGate(), RelayDiscoveryPolicy.Open);
        var capabilityStore = new FakeRelayMailboxCapabilityStore();
        var preKeyRepo = new InMemoryPreKeyRepo();
        var endpoint = new NearbyDiscoveryEndpoint(peerDir, capabilityStore, preKeyRepo, timeProvider, 20600, crypto);

        var peerId = PublicIdentityId.New();
        var (privKey, pubKey) = crypto.GenerateEphemeralKeyPair();
        int visibility = (int)PeerDiscoveryVisibility.Discoverable;
        long driftedTimestamp = timeProvider.UtcNow.AddMinutes(-10).ToUnixTimeMilliseconds();

        var payload = DiscoveryWireFraming.BuildVisibilitySignaturePayload(peerId, visibility, driftedTimestamp);
        var signature = crypto.SignEd25519(privKey.Span, payload);

        var request = new DiscoveryRegistrationProto
        {
            PeerId = ByteString.CopyFrom(peerId.ToByteArray()),
            Visibility = visibility,
            TimestampUnixMs = driftedTimestamp,
            IdentityKey = ByteString.CopyFrom(pubKey.Span),
            Signature = ByteString.CopyFrom(signature)
        };

        var act = async () => await endpoint.RegisterVisibility(request, new TestServerCallContext());
        await act.Should().ThrowAsync<RpcException>().Where(e => e.StatusCode == StatusCode.PermissionDenied);
    }

    [Test]
    public async Task RegisterVisibility_WithInvalidSignature_ThrowsPermissionDenied()
    {
        var crypto = new SignalCryptoEngine(new CryptographyOptions());
        var timeProvider = new FixedTimeProvider();
        var peerDir = new InMemoryRelayPeerDirectory(new DiscoveryAdmissionGate(), RelayDiscoveryPolicy.Open);
        var capabilityStore = new FakeRelayMailboxCapabilityStore();
        var preKeyRepo = new InMemoryPreKeyRepo();
        var endpoint = new NearbyDiscoveryEndpoint(peerDir, capabilityStore, preKeyRepo, timeProvider, 20600, crypto);

        var peerId = PublicIdentityId.New();
        var (_, pubKey) = crypto.GenerateEphemeralKeyPair();
        int visibility = (int)PeerDiscoveryVisibility.Discoverable;
        long timestamp = timeProvider.UtcNow.ToUnixTimeMilliseconds();

        var request = new DiscoveryRegistrationProto
        {
            PeerId = ByteString.CopyFrom(peerId.ToByteArray()),
            Visibility = visibility,
            TimestampUnixMs = timestamp,
            IdentityKey = ByteString.CopyFrom(pubKey.Span),
            Signature = ByteString.CopyFrom(new byte[64]) // forged zero signature
        };

        var act = async () => await endpoint.RegisterVisibility(request, new TestServerCallContext());
        await act.Should().ThrowAsync<RpcException>().Where(e => e.StatusCode == StatusCode.PermissionDenied);
    }

    [Test]
    public async Task RegisterVisibility_WithMismatchedPreKeyIdentity_ThrowsPermissionDenied()
    {
        var crypto = new SignalCryptoEngine(new CryptographyOptions());
        var timeProvider = new FixedTimeProvider();
        var peerDir = new InMemoryRelayPeerDirectory(new DiscoveryAdmissionGate(), RelayDiscoveryPolicy.Open);
        var capabilityStore = new FakeRelayMailboxCapabilityStore();
        var preKeyRepo = new InMemoryPreKeyRepo();
        var endpoint = new NearbyDiscoveryEndpoint(peerDir, capabilityStore, preKeyRepo, timeProvider, 20600, crypto);

        var peerId = PublicIdentityId.New();
        var (legitPriv, legitPub) = crypto.GenerateEphemeralKeyPair();
        var (attackerPriv, attackerPub) = crypto.GenerateEphemeralKeyPair();

        var routingToken = BlindedRoutingToken.New();
        var deliveryTokenBytes = RandomNumberGenerator.GetBytes(32);

        // Relay already holds a prekey bundle for the advertised mailbox, pinned to the legit key
        var bundle = RelayHostedPreKeyBundle.Rehydrate(
            routingToken,
            DeviceId.Primary,
            IdentityKey.FromSpan(legitPub.Span),
            DhPublicKey.FromSpan(new byte[32]),
            DeviceLinkProof.FromSpan(new byte[64]));
        await preKeyRepo.SaveAsync(bundle);

        // Attacker attempts to register visibility for the same mailbox with their own key
        int visibility = (int)PeerDiscoveryVisibility.Discoverable;
        long timestamp = timeProvider.UtcNow.ToUnixTimeMilliseconds();
        var payload = DiscoveryWireFraming.BuildVisibilitySignaturePayload(
            peerId, visibility, timestamp, routingToken.ToByteArray(), deliveryTokenBytes);
        var attackerSig = crypto.SignEd25519(attackerPriv.Span, payload);

        var request = new DiscoveryRegistrationProto
        {
            PeerId = ByteString.CopyFrom(peerId.ToByteArray()),
            Visibility = visibility,
            TimestampUnixMs = timestamp,
            IdentityKey = ByteString.CopyFrom(attackerPub.Span),
            Signature = ByteString.CopyFrom(attackerSig),
            RoutingToken = ByteString.CopyFrom(routingToken.ToByteArray()),
            DeliveryToken = ByteString.CopyFrom(deliveryTokenBytes)
        };

        var act = async () => await endpoint.RegisterVisibility(request, new TestServerCallContext());
        await act.Should().ThrowAsync<RpcException>().Where(e => e.StatusCode == StatusCode.PermissionDenied);
    }

    [Test]
    public async Task QueryNearby_ReturnsDiscoverablePeersExceptRequester()
    {
        var crypto = new SignalCryptoEngine(new CryptographyOptions());
        var timeProvider = new FixedTimeProvider();
        var peerDir = new InMemoryRelayPeerDirectory(new DiscoveryAdmissionGate(), RelayDiscoveryPolicy.Open);
        var capabilityStore = new FakeRelayMailboxCapabilityStore();
        var preKeyRepo = new InMemoryPreKeyRepo();
        var endpoint = new NearbyDiscoveryEndpoint(peerDir, capabilityStore, preKeyRepo, timeProvider, 20600, crypto);

        var peer1 = PublicIdentityId.New();
        var peer2 = PublicIdentityId.New();
        var (priv1, pub1) = crypto.GenerateEphemeralKeyPair();
        var (priv2, pub2) = crypto.GenerateEphemeralKeyPair();
        var routingToken1 = BlindedRoutingToken.New();
        var routingToken2 = BlindedRoutingToken.New();
        var deliveryToken1 = RandomNumberGenerator.GetBytes(32);
        var deliveryToken2 = RandomNumberGenerator.GetBytes(32);

        await preKeyRepo.SaveAsync(RelayHostedPreKeyBundle.Rehydrate(
            routingToken1,
            DeviceId.Primary,
            IdentityKey.FromSpan(pub1.Span),
            DhPublicKey.FromSpan(new byte[32]),
            DeviceLinkProof.FromSpan(new byte[64])));

        await preKeyRepo.SaveAsync(RelayHostedPreKeyBundle.Rehydrate(
            routingToken2,
            DeviceId.Primary,
            IdentityKey.FromSpan(pub2.Span),
            DhPublicKey.FromSpan(new byte[32]),
            DeviceLinkProof.FromSpan(new byte[64])));

        int visibility = (int)PeerDiscoveryVisibility.Discoverable;
        long timestamp = timeProvider.UtcNow.ToUnixTimeMilliseconds();

        var payload1 = DiscoveryWireFraming.BuildVisibilitySignaturePayload(
            peer1, visibility, timestamp, routingToken1.ToByteArray(), deliveryToken1);
        var sig1 = crypto.SignEd25519(priv1.Span, payload1);

        var payload2 = DiscoveryWireFraming.BuildVisibilitySignaturePayload(
            peer2, visibility, timestamp, routingToken2.ToByteArray(), deliveryToken2);
        var sig2 = crypto.SignEd25519(priv2.Span, payload2);

        await endpoint.RegisterVisibility(new DiscoveryRegistrationProto
        {
            PeerId = ByteString.CopyFrom(peer1.ToByteArray()),
            Visibility = visibility,
            TimestampUnixMs = timestamp,
            IdentityKey = ByteString.CopyFrom(pub1.Span),
            Signature = ByteString.CopyFrom(sig1),
            RoutingToken = ByteString.CopyFrom(routingToken1.ToByteArray()),
            DeliveryToken = ByteString.CopyFrom(deliveryToken1)
        }, new TestServerCallContext());

        await endpoint.RegisterVisibility(new DiscoveryRegistrationProto
        {
            PeerId = ByteString.CopyFrom(peer2.ToByteArray()),
            Visibility = visibility,
            TimestampUnixMs = timestamp,
            IdentityKey = ByteString.CopyFrom(pub2.Span),
            Signature = ByteString.CopyFrom(sig2),
            RoutingToken = ByteString.CopyFrom(routingToken2.ToByteArray()),
            DeliveryToken = ByteString.CopyFrom(deliveryToken2)
        }, new TestServerCallContext());

        var query = new NearbyDiscoveryQueryProto
        {
            RequesterId = ByteString.CopyFrom(peer1.ToByteArray()),
            RequestedLimit = 10
        };

        var resp = await endpoint.QueryNearby(query, new TestServerCallContext());
        resp.DiscoverablePeerIds.Should().Contain(ByteString.CopyFrom(peer2.ToByteArray()));
        resp.DiscoverablePeerIds.Should().NotContain(ByteString.CopyFrom(peer1.ToByteArray()));
    }
}

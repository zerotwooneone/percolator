namespace Percolator.Infrastructure.Tests.Transport.Endpoints;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using NUnit.Framework;
using Percolator.Domain.Delivery.Hosting;
using Percolator.Domain.Delivery.Ports;
using Percolator.Domain.Identities.ValueObjects;
using Percolator.Domain.Security.ValueObjects;
using Percolator.Infrastructure.Protos;
using Percolator.Infrastructure.Security;
using Percolator.Infrastructure.Transport;
using Percolator.Infrastructure.Transport.Endpoints;

[TestFixture]
public sealed class AuthenticatedRelayEndpointTests
{
    private sealed class InMemoryPreKeyRepo : IRelayPreKeyRepository
    {
        private readonly ConcurrentDictionary<(PublicIdentityId, DeviceId), RelayHostedPreKeyBundle> _bundles = new();

        public Task<RelayHostedPreKeyBundle?> GetAsync(PublicIdentityId ownerId, DeviceId deviceId, CancellationToken cancellationToken = default)
        {
            _bundles.TryGetValue((ownerId, deviceId), out var bundle);
            return Task.FromResult(bundle);
        }

        public Task<IReadOnlyList<RelayHostedPreKeyBundle>> GetAllForIdentityAsync(PublicIdentityId ownerId, CancellationToken cancellationToken = default)
        {
            IReadOnlyList<RelayHostedPreKeyBundle> list = new List<RelayHostedPreKeyBundle>(_bundles.Values);
            return Task.FromResult(list);
        }

        public Task<bool> HasActivePreKeyAsync(PublicIdentityId ownerId, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(_bundles.Count > 0);
        }

        public Task SaveAsync(RelayHostedPreKeyBundle bundle, CancellationToken cancellationToken = default)
        {
            _bundles[(bundle.OwnerId, bundle.DeviceId)] = bundle;
            return Task.CompletedTask;
        }

        public Task DeleteAsync(PublicIdentityId ownerId, DeviceId deviceId, CancellationToken cancellationToken = default)
        {
            _bundles.TryRemove((ownerId, deviceId), out _);
            return Task.CompletedTask;
        }
    }

    [Test]
    public async Task PublishPreKeyBundle_StaleTimestamp_RejectsRollbackReplay()
    {
        var capabilityStore = new InMemoryRelayMailboxCapabilityStore();
        var preKeyRepo = new InMemoryPreKeyRepo();
        var cryptoEngine = new SignalCryptoEngine();
        var endpoint = new AuthenticatedRelayEndpoint(capabilityStore, preKeyRepo, cryptoEngine);

        byte[] privId = Convert.FromHexString("9d61b19deffd5a60ba844af492ec2cc44449c5697b326919703bac031cae7f60");
        var pubId = IdentityKey.FromSpan(Convert.FromHexString("d75a980182b10ab7d54bfed3c964073a0ee172f3daa62325af021a68f707511a"));

        var (_, pubSigned) = cryptoEngine.GenerateEphemeralKeyPair();
        var prekeySig = cryptoEngine.SignEd25519(privId, pubSigned.Span);

        var ownerId = PublicIdentityId.New();
        var deviceId = 1u;
        var now = DateTimeOffset.UtcNow;

        // 1. Initial valid bundle publication at T=now
        var payload1 = new PreKeyBundlePayloadProto
        {
            OwnerIdentityId = ByteString.CopyFrom(ownerId.Value.ToByteArray()),
            DeviceId = deviceId,
            IdentityKey = ByteString.CopyFrom(pubId.Span),
            SignedPrekeyId = 1,
            SignedPrekey = ByteString.CopyFrom(pubSigned.Span),
            PrekeySignature = ByteString.CopyFrom(prekeySig),
            PublishedAtUtc = Timestamp.FromDateTimeOffset(now)
        };

        byte[] payloadBytes1 = payload1.ToByteArray();
        byte[] sig1 = cryptoEngine.SignEd25519(privId, payloadBytes1);

        var request1 = new PublishPreKeyBundleRequest
        {
            PayloadBytes = ByteString.CopyFrom(payloadBytes1),
            Signature = ByteString.CopyFrom(sig1)
        };

        var resp1 = await endpoint.PublishPreKeyBundle(request1, new TestServerCallContext());
        resp1.Success.Should().BeTrue();

        // 2. Replay attack with older timestamp (T=now - 1 hour)
        var stalePayload = new PreKeyBundlePayloadProto
        {
            OwnerIdentityId = ByteString.CopyFrom(ownerId.Value.ToByteArray()),
            DeviceId = deviceId,
            IdentityKey = ByteString.CopyFrom(pubId.Span),
            SignedPrekeyId = 1,
            SignedPrekey = ByteString.CopyFrom(pubSigned.Span),
            PrekeySignature = ByteString.CopyFrom(prekeySig),
            PublishedAtUtc = Timestamp.FromDateTimeOffset(now.AddHours(-1)) // Stale timestamp
        };

        byte[] stalePayloadBytes = stalePayload.ToByteArray();
        byte[] staleSig = cryptoEngine.SignEd25519(privId, stalePayloadBytes);

        var staleRequest = new PublishPreKeyBundleRequest
        {
            PayloadBytes = ByteString.CopyFrom(stalePayloadBytes),
            Signature = ByteString.CopyFrom(staleSig)
        };

        var staleResp = await endpoint.PublishPreKeyBundle(staleRequest, new TestServerCallContext());
        staleResp.Success.Should().BeFalse();

        // 3. Newer timestamp (T=now + 1 hour) succeeds
        var newerPayload = new PreKeyBundlePayloadProto
        {
            OwnerIdentityId = ByteString.CopyFrom(ownerId.Value.ToByteArray()),
            DeviceId = deviceId,
            IdentityKey = ByteString.CopyFrom(pubId.Span),
            SignedPrekeyId = 2,
            SignedPrekey = ByteString.CopyFrom(pubSigned.Span),
            PrekeySignature = ByteString.CopyFrom(prekeySig),
            PublishedAtUtc = Timestamp.FromDateTimeOffset(now.AddHours(1))
        };

        byte[] newerPayloadBytes = newerPayload.ToByteArray();
        byte[] newerSig = cryptoEngine.SignEd25519(privId, newerPayloadBytes);

        var newerRequest = new PublishPreKeyBundleRequest
        {
            PayloadBytes = ByteString.CopyFrom(newerPayloadBytes),
            Signature = ByteString.CopyFrom(newerSig)
        };

        var newerResp = await endpoint.PublishPreKeyBundle(newerRequest, new TestServerCallContext());
        newerResp.Success.Should().BeTrue();
    }
}

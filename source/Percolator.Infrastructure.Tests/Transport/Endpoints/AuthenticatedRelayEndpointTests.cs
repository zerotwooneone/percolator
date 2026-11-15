using Percolator.Application.Ports;

namespace Percolator.Infrastructure.Tests.Transport.Endpoints;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using NUnit.Framework;
using Percolator.Domain.Common;
using Percolator.Domain.Delivery.Hosting;
using Percolator.Domain.Delivery.Ports;
using Percolator.Domain.Delivery.ValueObjects;
using Percolator.Domain.Identities.ValueObjects;
using Percolator.Domain.Security.ValueObjects;
using Percolator.Infrastructure.Protos;
using Percolator.Infrastructure.Security;
using Percolator.Infrastructure.Tests.Fakes;
using Percolator.Infrastructure.Transport;
using Percolator.Infrastructure.Transport.Endpoints;
using Percolator.PluginSdk;
using Signal.Interop;

[TestFixture]
public sealed class AuthenticatedRelayEndpointTests
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

    private sealed class InMemoryProfileRepo : IRelayProfileRepository
    {
        public ValueTask<RelayHostedProfileSlot?> GetSlotAsync(BlindedProfileLocator locator, CancellationToken ct = default)
            => ValueTask.FromResult<RelayHostedProfileSlot?>(null);

        public ValueTask<DomainResult> SaveSlotAsync(RelayHostedProfileSlot slot, CancellationToken ct = default)
            => ValueTask.FromResult(DomainResult.Success());

        public ValueTask<DomainResult> DeleteSlotAsync(BlindedProfileLocator locator, ProfileWriteToken writeToken, CancellationToken ct = default)
            => ValueTask.FromResult(DomainResult.Success());
    }

    private sealed class InMemoryChunkStore : ILocalChunkStore
    {
        public ValueTask<bool> HasBlobAsync(BlobId blobId, CancellationToken ct = default)
            => ValueTask.FromResult(false);

        public ValueTask<Stream> OpenReadAsync(BlobId blobId, CancellationToken ct = default)
            => throw new FileNotFoundException();

        public ValueTask SaveBlobAsync(BlobId blobId, Stream contentStream, CancellationToken ct = default)
            => ValueTask.CompletedTask;

        public ValueTask DeleteBlobAsync(BlobId blobId, CancellationToken ct = default)
            => ValueTask.CompletedTask;
    }

    private static PublishPreKeyBundleRequest BuildPublishRequest(
        byte[] routingToken,
        byte[] drainToken,
        uint deviceId,
        IdentityKey identityKey,
        DhPublicKey signedPreKey,
        byte[] prekeySignature,
        IReadOnlyList<(uint KeyId, DhPublicKey Key)> opks)
    {
        var request = new PublishPreKeyBundleRequest
        {
            RoutingToken = ByteString.CopyFrom(routingToken),
            DrainToken = ByteString.CopyFrom(drainToken),
            DeviceId = deviceId,
            IdentityKey = ByteString.CopyFrom(identityKey.Span),
            SignedPrekeyId = 1,
            SignedPrekey = ByteString.CopyFrom(signedPreKey.Span),
            PrekeySignature = ByteString.CopyFrom(prekeySignature)
        };
        foreach (var (keyId, key) in opks)
        {
            request.OneTimePrekeys.Add(new OneTimePreKeyProto
            {
                KeyId = keyId,
                PublicKey = ByteString.CopyFrom(key.Span)
            });
        }
        return request;
    }

    [Test]
    public async Task PublishPreKeyBundle_WithValidDrainToken_EnablesFetchByRoutingToken()
    {
        var capabilityStore = new FakeRelayMailboxCapabilityStore();
        var preKeyRepo = new InMemoryPreKeyRepo();
        var cryptoEngine = new SignalCryptoEngine(new CryptographyOptions());
        var endpoint = new AuthenticatedRelayEndpoint(capabilityStore, preKeyRepo, cryptoEngine);
        var unauthEndpoint = new UnauthenticatedDeliveryEndpoint(
            new InMemoryProfileRepo(), preKeyRepo, new InMemoryChunkStore(), capabilityStore);

        var routingToken = new byte[16];
        var deliveryToken = new byte[32];
        var drainToken = new byte[32];
        RandomNumberGenerator.Fill(routingToken);
        RandomNumberGenerator.Fill(deliveryToken);
        RandomNumberGenerator.Fill(drainToken);

        await capabilityStore.RegisterAsync(routingToken, deliveryToken, drainToken);

        byte[] privId = new byte[32];
        byte[] pubIdBytes = new byte[32];
        SignalCrypto.GenerateEd25519KeyPair(privId, pubIdBytes);
        var pubId = IdentityKey.FromSpan(pubIdBytes);

        var deviceId = 1u;

        var (_, pubSigned) = cryptoEngine.GenerateEphemeralKeyPair();
        var prekeySig = cryptoEngine.SignEd25519(privId, pubSigned.Span);
        var (_, opkPub) = cryptoEngine.GenerateEphemeralKeyPair();

        var request = BuildPublishRequest(
            routingToken,
            drainToken,
            deviceId,
            pubId,
            pubSigned,
            prekeySig,
            new List<(uint, DhPublicKey)> { (7u, opkPub) });

        var response = await endpoint.PublishPreKeyBundle(request, new TestServerCallContext());
        response.Success.Should().BeTrue();

        // The publish alone makes the bundle fetchable by (routing_token, delivery_token) —
        // no owner-identity association exists anywhere.
        var fetch = await unauthEndpoint.FetchPreKeyBundle(
            new FetchPreKeyBundleRequest
            {
                RecipientRoutingToken = ByteString.CopyFrom(routingToken),
                DeliveryToken = ByteString.CopyFrom(deliveryToken)
            },
            new TestServerCallContext());

        fetch.OneTimePrekeyId.Should().Be(7u);
    }

    [Test]
    public async Task PublishPreKeyBundle_WithMismatchedDrainToken_Rejected()
    {
        var capabilityStore = new FakeRelayMailboxCapabilityStore();
        var preKeyRepo = new InMemoryPreKeyRepo();
        var cryptoEngine = new SignalCryptoEngine(new CryptographyOptions());
        var endpoint = new AuthenticatedRelayEndpoint(capabilityStore, preKeyRepo, cryptoEngine);

        var routingToken = new byte[16];
        var deliveryToken = new byte[32];
        var drainToken = new byte[32];
        var wrongDrainToken = new byte[32];
        RandomNumberGenerator.Fill(routingToken);
        RandomNumberGenerator.Fill(deliveryToken);
        RandomNumberGenerator.Fill(drainToken);
        RandomNumberGenerator.Fill(wrongDrainToken);

        await capabilityStore.RegisterAsync(routingToken, deliveryToken, drainToken);

        byte[] privId = new byte[32];
        byte[] pubIdBytes = new byte[32];
        SignalCrypto.GenerateEd25519KeyPair(privId, pubIdBytes);
        var pubId = IdentityKey.FromSpan(pubIdBytes);

        var (_, pubSigned) = cryptoEngine.GenerateEphemeralKeyPair();
        var prekeySig = cryptoEngine.SignEd25519(privId, pubSigned.Span);

        var request = BuildPublishRequest(
            routingToken,
            wrongDrainToken,
            1u,
            pubId,
            pubSigned,
            prekeySig,
            new List<(uint, DhPublicKey)>());

        var response = await endpoint.PublishPreKeyBundle(request, new TestServerCallContext());
        response.Success.Should().BeFalse();

        (await preKeyRepo.GetAsync(BlindedRoutingToken.FromBytes(routingToken), new DeviceId(1u))).Should().BeNull();
    }

    [Test]
    public async Task PublishPreKeyBundle_SecondPublish_ReplenishesOnlyNewOneTimePreKeys()
    {
        var capabilityStore = new FakeRelayMailboxCapabilityStore();
        var preKeyRepo = new InMemoryPreKeyRepo();
        var cryptoEngine = new SignalCryptoEngine(new CryptographyOptions());
        var endpoint = new AuthenticatedRelayEndpoint(capabilityStore, preKeyRepo, cryptoEngine);

        var routingToken = new byte[16];
        var deliveryToken = new byte[32];
        var drainToken = new byte[32];
        RandomNumberGenerator.Fill(routingToken);
        RandomNumberGenerator.Fill(deliveryToken);
        RandomNumberGenerator.Fill(drainToken);

        await capabilityStore.RegisterAsync(routingToken, deliveryToken, drainToken);

        byte[] privId = new byte[32];
        byte[] pubIdBytes = new byte[32];
        SignalCrypto.GenerateEd25519KeyPair(privId, pubIdBytes);
        var pubId = IdentityKey.FromSpan(pubIdBytes);

        var deviceId = new DeviceId(1u);

        var (_, pubSigned1) = cryptoEngine.GenerateEphemeralKeyPair();
        var prekeySig1 = cryptoEngine.SignEd25519(privId, pubSigned1.Span);

        // First publish: 25 OPKs (KeyIds 1-25).
        var firstOpks = new List<(uint, DhPublicKey)>();
        for (uint i = 1; i <= 25; i++)
        {
            firstOpks.Add((i, cryptoEngine.GenerateEphemeralKeyPair().PublicKey));
        }

        var request1 = BuildPublishRequest(
            routingToken, drainToken, deviceId.Value, pubId, pubSigned1, prekeySig1,
            firstOpks);
        (await endpoint.PublishPreKeyBundle(request1, new TestServerCallContext())).Success.Should().BeTrue();

        // Consume one OPK — relay now holds 24.
        var routingTokenVo = BlindedRoutingToken.FromBytes(routingToken);
        var hosted = await preKeyRepo.GetAsync(routingTokenVo, deviceId);
        hosted!.ConsumeBundle().IsSuccess.Should().BeTrue();
        hosted.AvailableOneTimePreKeyCount.Should().Be(24);
        await preKeyRepo.SaveAsync(hosted);

        // Second publish: rotated signed pre-key + 25 fresh OPKs (KeyIds 26-50).
        var (_, pubSigned2) = cryptoEngine.GenerateEphemeralKeyPair();
        var prekeySig2 = cryptoEngine.SignEd25519(privId, pubSigned2.Span);
        var secondOpks = new List<(uint, DhPublicKey)>();
        for (uint i = 26; i <= 50; i++)
        {
            secondOpks.Add((i, cryptoEngine.GenerateEphemeralKeyPair().PublicKey));
        }

        var request2 = BuildPublishRequest(
            routingToken, drainToken, deviceId.Value, pubId, pubSigned2, prekeySig2,
            secondOpks);
        (await endpoint.PublishPreKeyBundle(request2, new TestServerCallContext())).Success.Should().BeTrue();

        var replenished = await preKeyRepo.GetAsync(routingTokenVo, deviceId);
        replenished!.AvailableOneTimePreKeyCount.Should().Be(49);
        replenished.SignedPreKey.Span.ToArray().Should().Equal(pubSigned2.Span.ToArray());
    }

    [Test]
    public async Task PublishPreKeyBundle_IdenticalRepublish_IsIdempotent_NoDuplicateOpks()
    {
        var capabilityStore = new FakeRelayMailboxCapabilityStore();
        var preKeyRepo = new InMemoryPreKeyRepo();
        var cryptoEngine = new SignalCryptoEngine(new CryptographyOptions());
        var endpoint = new AuthenticatedRelayEndpoint(capabilityStore, preKeyRepo, cryptoEngine);

        var routingToken = new byte[16];
        var deliveryToken = new byte[32];
        var drainToken = new byte[32];
        RandomNumberGenerator.Fill(routingToken);
        RandomNumberGenerator.Fill(deliveryToken);
        RandomNumberGenerator.Fill(drainToken);

        await capabilityStore.RegisterAsync(routingToken, deliveryToken, drainToken);

        byte[] privId = new byte[32];
        byte[] pubIdBytes = new byte[32];
        SignalCrypto.GenerateEd25519KeyPair(privId, pubIdBytes);
        var pubId = IdentityKey.FromSpan(pubIdBytes);

        var deviceId = new DeviceId(1u);
        var routingTokenVo = BlindedRoutingToken.FromBytes(routingToken);

        var (_, pubSigned) = cryptoEngine.GenerateEphemeralKeyPair();
        var prekeySig = cryptoEngine.SignEd25519(privId, pubSigned.Span);
        var opks = new List<(uint, DhPublicKey)>
        {
            (1u, cryptoEngine.GenerateEphemeralKeyPair().PublicKey),
            (2u, cryptoEngine.GenerateEphemeralKeyPair().PublicKey)
        };

        var request = BuildPublishRequest(
            routingToken, drainToken, deviceId.Value, pubId, pubSigned, prekeySig, opks);

        // Act: publish the exact same request twice (e.g., client retry after timeout).
        (await endpoint.PublishPreKeyBundle(request, new TestServerCallContext())).Success.Should().BeTrue();
        (await endpoint.PublishPreKeyBundle(request, new TestServerCallContext())).Success.Should().BeTrue();

        // Assert: retry must not duplicate OPKs — writes are idempotent per KeyId.
        var stored = await preKeyRepo.GetAsync(routingTokenVo, deviceId);
        stored!.AvailableOneTimePreKeyCount.Should().Be(2);
    }

    [Test]
    public async Task PublishPreKeyBundle_SecondPublishWithDifferentIdentityKey_Rejected()
    {
        var capabilityStore = new FakeRelayMailboxCapabilityStore();
        var preKeyRepo = new InMemoryPreKeyRepo();
        var cryptoEngine = new SignalCryptoEngine(new CryptographyOptions());
        var endpoint = new AuthenticatedRelayEndpoint(capabilityStore, preKeyRepo, cryptoEngine);

        var routingToken = new byte[16];
        var deliveryToken = new byte[32];
        var drainToken = new byte[32];
        RandomNumberGenerator.Fill(routingToken);
        RandomNumberGenerator.Fill(deliveryToken);
        RandomNumberGenerator.Fill(drainToken);

        await capabilityStore.RegisterAsync(routingToken, deliveryToken, drainToken);

        byte[] privId = new byte[32];
        byte[] pubIdBytes = new byte[32];
        SignalCrypto.GenerateEd25519KeyPair(privId, pubIdBytes);
        var pubId = IdentityKey.FromSpan(pubIdBytes);

        var (_, pubSigned) = cryptoEngine.GenerateEphemeralKeyPair();
        var prekeySig = cryptoEngine.SignEd25519(privId, pubSigned.Span);

        var request1 = BuildPublishRequest(
            routingToken, drainToken, 1u, pubId, pubSigned, prekeySig,
            new List<(uint, DhPublicKey)>());
        (await endpoint.PublishPreKeyBundle(request1, new TestServerCallContext())).Success.Should().BeTrue();

        // Identity-key substitution is rejected even with a valid drain token (pinning).
        byte[] attackerPriv = new byte[32];
        byte[] attackerPubBytes = new byte[32];
        SignalCrypto.GenerateEd25519KeyPair(attackerPriv, attackerPubBytes);
        var attackerPub = IdentityKey.FromSpan(attackerPubBytes);
        var attackerPrekeySig = cryptoEngine.SignEd25519(attackerPriv, pubSigned.Span);

        var request2 = BuildPublishRequest(
            routingToken, drainToken, 1u, attackerPub, pubSigned, attackerPrekeySig,
            new List<(uint, DhPublicKey)>());
        (await endpoint.PublishPreKeyBundle(request2, new TestServerCallContext())).Success.Should().BeFalse();

        var stored = await preKeyRepo.GetAsync(BlindedRoutingToken.FromBytes(routingToken), DeviceId.Primary);
        stored!.IdentityKey.Span.ToArray().Should().Equal(pubId.Span.ToArray());
    }

    [Test]
    public async Task RegisterMailbox_ExistingRoutingTokenWithWrongDrainToken_ThrowsPermissionDenied()
    {
        var capabilityStore = new FakeRelayMailboxCapabilityStore();
        var preKeyRepo = new InMemoryPreKeyRepo();
        var cryptoEngine = new SignalCryptoEngine(new CryptographyOptions());
        var endpoint = new AuthenticatedRelayEndpoint(capabilityStore, preKeyRepo, cryptoEngine);

        var routingToken = new byte[16];
        var deliveryToken1 = new byte[32];
        var drainToken1 = new byte[32];
        RandomNumberGenerator.Fill(routingToken);
        RandomNumberGenerator.Fill(deliveryToken1);
        RandomNumberGenerator.Fill(drainToken1);

        // 1. Initial registration
        var initialReq = new RegisterMailboxRequest
        {
            RoutingToken = ByteString.CopyFrom(routingToken),
            AuthorizedDeliveryToken = ByteString.CopyFrom(deliveryToken1),
            DrainToken = ByteString.CopyFrom(drainToken1)
        };
        var resp1 = await endpoint.RegisterMailbox(initialReq, new TestServerCallContext());
        resp1.Success.Should().BeTrue();

        // 2. Hijack attempt with wrong existing drain token (or empty)
        var deliveryToken2 = new byte[32];
        var drainToken2 = new byte[32];
        RandomNumberGenerator.Fill(deliveryToken2);
        RandomNumberGenerator.Fill(drainToken2);

        var hijackReq = new RegisterMailboxRequest
        {
            RoutingToken = ByteString.CopyFrom(routingToken),
            AuthorizedDeliveryToken = ByteString.CopyFrom(deliveryToken2),
            DrainToken = ByteString.CopyFrom(drainToken2)
        };

        Func<Task> act = async () => await endpoint.RegisterMailbox(hijackReq, new TestServerCallContext());
        var ex = await act.Should().ThrowAsync<RpcException>();
        ex.Which.StatusCode.Should().Be(StatusCode.PermissionDenied);
    }

    [Test]
    public async Task RegisterMailbox_ExistingRoutingTokenWithCorrectExistingDrainToken_Succeeds()
    {
        var capabilityStore = new FakeRelayMailboxCapabilityStore();
        var preKeyRepo = new InMemoryPreKeyRepo();
        var cryptoEngine = new SignalCryptoEngine(new CryptographyOptions());
        var endpoint = new AuthenticatedRelayEndpoint(capabilityStore, preKeyRepo, cryptoEngine);

        var routingToken = new byte[16];
        var deliveryToken1 = new byte[32];
        var drainToken1 = new byte[32];
        RandomNumberGenerator.Fill(routingToken);
        RandomNumberGenerator.Fill(deliveryToken1);
        RandomNumberGenerator.Fill(drainToken1);

        // 1. Initial registration
        var initialReq = new RegisterMailboxRequest
        {
            RoutingToken = ByteString.CopyFrom(routingToken),
            AuthorizedDeliveryToken = ByteString.CopyFrom(deliveryToken1),
            DrainToken = ByteString.CopyFrom(drainToken1)
        };
        var resp1 = await endpoint.RegisterMailbox(initialReq, new TestServerCallContext());
        resp1.Success.Should().BeTrue();

        // 2. Legitimate owner rotation with ExistingDrainToken
        var deliveryToken2 = new byte[32];
        var drainToken2 = new byte[32];
        RandomNumberGenerator.Fill(deliveryToken2);
        RandomNumberGenerator.Fill(drainToken2);

        var rotateReq = new RegisterMailboxRequest
        {
            RoutingToken = ByteString.CopyFrom(routingToken),
            AuthorizedDeliveryToken = ByteString.CopyFrom(deliveryToken2),
            DrainToken = ByteString.CopyFrom(drainToken2),
            ExistingDrainToken = ByteString.CopyFrom(drainToken1)
        };

        var resp2 = await endpoint.RegisterMailbox(rotateReq, new TestServerCallContext());
        resp2.Success.Should().BeTrue();

        // Verify new drain token is active
        (await capabilityStore.VerifyDrainTokenAsync(routingToken, drainToken2)).Should().BeTrue();
    }

    [Test]
    public async Task ConnectMailboxStream_StreamsLiveEnvelopes_AndAcknowledgesViaClient()
    {
        var capabilityStore = new FakeRelayMailboxCapabilityStore();
        var preKeyRepo = new InMemoryPreKeyRepo();
        var cryptoEngine = new SignalCryptoEngine(new CryptographyOptions());
        var endpoint = new AuthenticatedRelayEndpoint(capabilityStore, preKeyRepo, cryptoEngine);

        var routingToken = new byte[16];
        var deliveryToken = new byte[32];
        var drainToken = new byte[32];
        RandomNumberGenerator.Fill(routingToken);
        RandomNumberGenerator.Fill(deliveryToken);
        RandomNumberGenerator.Fill(drainToken);

        await capabilityStore.RegisterAsync(routingToken, deliveryToken, drainToken);

        var clientChannel = Channel.CreateUnbounded<MailboxStreamClientMessage>();
        var serverWriter = new TestServerStreamWriter<MailboxStreamServerMessage>();
        using var cts = new CancellationTokenSource();
        var serverContext = new TestServerCallContext(cts.Token);

        var reader = new ChannelAsyncStreamReader<MailboxStreamClientMessage>(clientChannel.Reader);

        var streamTask = Task.Run(async () =>
        {
            await endpoint.ConnectMailboxStream(reader, serverWriter, serverContext);
        });

        using var stepTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        // 1. Await AuthChallenge deterministically
        var challengeMsg = await serverWriter.ReadNextAsync(stepTimeout.Token);
        var challenge = challengeMsg.AuthChallenge;
        challenge.Should().NotBeNull();

        // 2. Compute HMAC and send AuthResponse
        using var hmac = new HMACSHA256(drainToken);
        byte[] challengeHmac = hmac.ComputeHash(challenge.Nonce.ToByteArray());

        await clientChannel.Writer.WriteAsync(new MailboxStreamClientMessage
        {
            AuthResponse = new AuthResponseProto
            {
                RoutingToken = ByteString.CopyFrom(routingToken),
                ChallengeHmac = ByteString.CopyFrom(challengeHmac)
            }
        });

        // 3. Await AuthResult deterministically
        var authResultMsg = await serverWriter.ReadNextAsync(stepTimeout.Token);
        var authResult = authResultMsg.AuthResult;
        authResult.Should().NotBeNull();
        authResult.Authenticated.Should().BeTrue();

        // 4. Enqueue a message live
        var queued = new QueuedSealedEnvelopeProto
        {
            QueueId = "q-12345",
            EnvelopePayload = ByteString.CopyFrom(new byte[] { 1, 2, 3, 4 }),
            EnqueuedAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow)
        };

        await capabilityStore.EnqueueEnvelopeAsync(routingToken, queued);

        // 5. Await live message deterministically
        var liveEnvelopeMsg = await serverWriter.ReadNextAsync(stepTimeout.Token);
        var envMsg = liveEnvelopeMsg.Envelope;
        envMsg.Should().NotBeNull();
        envMsg.QueueId.Should().Be("q-12345");

        // Verify envelope is currently in store
        (await capabilityStore.GetEnvelopesAsync(routingToken)).Count.Should().Be(1);

        // 6. Send ACK from client
        await clientChannel.Writer.WriteAsync(new MailboxStreamClientMessage
        {
            Ack = new MailboxAckProto
            {
                QueueId = "q-12345",
                Acknowledged = true
            }
        });

        // Await ACK processing with bounded timeout
        using var ackTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while ((await capabilityStore.GetEnvelopesAsync(routingToken)).Count > 0 && !ackTimeout.IsCancellationRequested)
        {
            await Task.Yield();
        }

        (await capabilityStore.GetEnvelopesAsync(routingToken)).Count.Should().Be(0);

        // 7. Complete client stream to shut down cleanly
        clientChannel.Writer.Complete();
        await streamTask;
    }

    [Test]
    public async Task ConnectMailboxStream_ClientDisconnectsWithoutAck_RetainsOfflineEnvelopesInStore()
    {
        var capabilityStore = new FakeRelayMailboxCapabilityStore();
        var preKeyRepo = new InMemoryPreKeyRepo();
        var cryptoEngine = new SignalCryptoEngine(new CryptographyOptions());
        var endpoint = new AuthenticatedRelayEndpoint(capabilityStore, preKeyRepo, cryptoEngine);

        var routingToken = new byte[16];
        var deliveryToken = new byte[32];
        var drainToken = new byte[32];
        RandomNumberGenerator.Fill(routingToken);
        RandomNumberGenerator.Fill(deliveryToken);
        RandomNumberGenerator.Fill(drainToken);

        await capabilityStore.RegisterAsync(routingToken, deliveryToken, drainToken);

        // Pre-enqueue an offline envelope
        var preEnqueued = new QueuedSealedEnvelopeProto
        {
            QueueId = "q-offline-01",
            EnvelopePayload = ByteString.CopyFrom(new byte[] { 5, 6, 7, 8 }),
            EnqueuedAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow)
        };
        await capabilityStore.EnqueueEnvelopeAsync(routingToken, preEnqueued);
        (await capabilityStore.GetEnvelopesAsync(routingToken)).Count.Should().Be(1);

        var clientChannel = Channel.CreateUnbounded<MailboxStreamClientMessage>();
        var serverWriter = new TestServerStreamWriter<MailboxStreamServerMessage>();
        using var cts = new CancellationTokenSource();
        var serverContext = new TestServerCallContext(cts.Token);

        var reader = new ChannelAsyncStreamReader<MailboxStreamClientMessage>(clientChannel.Reader);

        var streamTask = Task.Run(async () =>
        {
            await endpoint.ConnectMailboxStream(reader, serverWriter, serverContext);
        });

        using var stepTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        // 1. Challenge & Authenticate
        var challengeMsg = await serverWriter.ReadNextAsync(stepTimeout.Token);
        using var hmac = new HMACSHA256(drainToken);
        byte[] challengeHmac = hmac.ComputeHash(challengeMsg.AuthChallenge.Nonce.ToByteArray());

        await clientChannel.Writer.WriteAsync(new MailboxStreamClientMessage
        {
            AuthResponse = new AuthResponseProto
            {
                RoutingToken = ByteString.CopyFrom(routingToken),
                ChallengeHmac = ByteString.CopyFrom(challengeHmac)
            }
        });

        var authResultMsg = await serverWriter.ReadNextAsync(stepTimeout.Token);
        authResultMsg.AuthResult.Authenticated.Should().BeTrue();

        // 2. Receive offline envelope
        var offlineEnvelopeMsg = await serverWriter.ReadNextAsync(stepTimeout.Token);
        offlineEnvelopeMsg.Envelope.QueueId.Should().Be("q-offline-01");

        // 3. Client disconnects abruptly WITHOUT acknowledging the envelope
        clientChannel.Writer.Complete();
        await streamTask;

        // 4. Verify envelope is STILL retained in the capability store for at-least-once delivery
        (await capabilityStore.GetEnvelopesAsync(routingToken)).Count.Should().Be(1);
        var stored = await capabilityStore.GetEnvelopesAsync(routingToken);
        stored.Should().ContainSingle(e => e.QueueId == "q-offline-01");
    }

    [Test]
    public async Task ConnectMailboxStream_EnvelopeArrivingDuringInitialReplay_DeliveredExactlyOnce()
    {
        var inner = new FakeRelayMailboxCapabilityStore();
        // Hold the initial snapshot open so an enqueue lands in BOTH the live channel
        // (subscription is already registered) and the eventual snapshot — the exact
        // window the initialQueueIds filter exists to close.
        var capabilityStore = new GatedSnapshotCapabilityStore(inner);
        var preKeyRepo = new InMemoryPreKeyRepo();
        var cryptoEngine = new SignalCryptoEngine(new CryptographyOptions());
        var endpoint = new AuthenticatedRelayEndpoint(capabilityStore, preKeyRepo, cryptoEngine);

        var routingToken = new byte[16];
        var deliveryToken = new byte[32];
        var drainToken = new byte[32];
        RandomNumberGenerator.Fill(routingToken);
        RandomNumberGenerator.Fill(deliveryToken);
        RandomNumberGenerator.Fill(drainToken);
        await inner.RegisterAsync(routingToken, deliveryToken, drainToken);

        capabilityStore.Arm();

        var clientChannel = Channel.CreateUnbounded<MailboxStreamClientMessage>();
        var serverWriter = new TestServerStreamWriter<MailboxStreamServerMessage>();
        var serverContext = new TestServerCallContext();
        var reader = new ChannelAsyncStreamReader<MailboxStreamClientMessage>(clientChannel.Reader);
        var streamTask = Task.Run(async () =>
            await endpoint.ConnectMailboxStream(reader, serverWriter, serverContext));

        using var stepTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var challengeMsg = await serverWriter.ReadNextAsync(stepTimeout.Token);
        using var hmac = new HMACSHA256(drainToken);
        byte[] challengeHmac = hmac.ComputeHash(challengeMsg.AuthChallenge.Nonce.ToByteArray());
        await clientChannel.Writer.WriteAsync(new MailboxStreamClientMessage
        {
            AuthResponse = new AuthResponseProto
            {
                RoutingToken = ByteString.CopyFrom(routingToken),
                ChallengeHmac = ByteString.CopyFrom(challengeHmac)
            }
        });
        var authResultMsg = await serverWriter.ReadNextAsync(stepTimeout.Token);
        authResultMsg.AuthResult.Authenticated.Should().BeTrue();

        // Enqueue inside the snapshot window: persisted AND live-notified.
        await inner.EnqueueEnvelopeAsync(routingToken, new QueuedSealedEnvelopeProto
        {
            QueueId = "q-race",
            EnvelopePayload = ByteString.CopyFrom(new byte[] { 1 }),
            EnqueuedAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow)
        });

        // Release the snapshot — "q-race" appears in both the initial batch and the
        // live channel; the endpoint must deliver it exactly once.
        capabilityStore.Release();

        var firstEnvelope = await serverWriter.ReadNextAsync(stepTimeout.Token);
        firstEnvelope.Envelope.QueueId.Should().Be("q-race");

        // No duplicate copy should follow.
        using var shortTimeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        Func<Task> act = async () => await serverWriter.ReadNextAsync(shortTimeout.Token);
        await act.Should().ThrowAsync<OperationCanceledException>(
            "the live-channel copy of an already-replayed envelope must be suppressed");

        clientChannel.Writer.Complete();
        await streamTask;
    }

    [Test]
    public async Task ConnectMailboxStream_LiveEnqueueFloodBeyondCapacity_NoEnvelopeLost()
    {
        var capabilityStore = new FakeRelayMailboxCapabilityStore();
        var preKeyRepo = new InMemoryPreKeyRepo();
        var cryptoEngine = new SignalCryptoEngine(new CryptographyOptions());
        var endpoint = new AuthenticatedRelayEndpoint(capabilityStore, preKeyRepo, cryptoEngine);

        var routingToken = new byte[16];
        var deliveryToken = new byte[32];
        var drainToken = new byte[32];
        RandomNumberGenerator.Fill(routingToken);
        RandomNumberGenerator.Fill(deliveryToken);
        RandomNumberGenerator.Fill(drainToken);
        await capabilityStore.RegisterAsync(routingToken, deliveryToken, drainToken);

        const int floodCount = 300; // beyond the 256-item live channel bound

        var clientChannel = Channel.CreateUnbounded<MailboxStreamClientMessage>();
        var serverWriter = new TestServerStreamWriter<MailboxStreamServerMessage>();
        var serverContext = new TestServerCallContext();
        var reader = new ChannelAsyncStreamReader<MailboxStreamClientMessage>(clientChannel.Reader);
        var streamTask = Task.Run(async () =>
            await endpoint.ConnectMailboxStream(reader, serverWriter, serverContext));

        using var stepTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var challengeMsg = await serverWriter.ReadNextAsync(stepTimeout.Token);
        using var hmac = new HMACSHA256(drainToken);
        await clientChannel.Writer.WriteAsync(new MailboxStreamClientMessage
        {
            AuthResponse = new AuthResponseProto
            {
                RoutingToken = ByteString.CopyFrom(routingToken),
                ChallengeHmac = ByteString.CopyFrom(hmac.ComputeHash(challengeMsg.AuthChallenge.Nonce.ToByteArray()))
            }
        });
        var authResultMsg = await serverWriter.ReadNextAsync(stepTimeout.Token);
        authResultMsg.AuthResult.Authenticated.Should().BeTrue();

        for (var i = 0; i < floodCount; i++)
        {
            await capabilityStore.EnqueueEnvelopeAsync(routingToken, new QueuedSealedEnvelopeProto
            {
                QueueId = $"q-flood-{i}",
                EnvelopePayload = ByteString.CopyFrom(new byte[] { 1 }),
                EnqueuedAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow)
            });
        }

        // The bounded live channel may drop notifications under flood, but every
        // envelope is persisted first — nothing is lost from the store.
        clientChannel.Writer.Complete();
        await streamTask;
        (await capabilityStore.GetEnvelopesAsync(routingToken)).Count.Should().Be(floodCount);

        // A reconnect's initial replay delivers all of them, exactly once each.
        var clientChannel2 = Channel.CreateUnbounded<MailboxStreamClientMessage>();
        var serverWriter2 = new TestServerStreamWriter<MailboxStreamServerMessage>();
        var reader2 = new ChannelAsyncStreamReader<MailboxStreamClientMessage>(clientChannel2.Reader);
        var streamTask2 = Task.Run(async () =>
            await endpoint.ConnectMailboxStream(reader2, serverWriter2, serverContext));

        var challengeMsg2 = await serverWriter2.ReadNextAsync(stepTimeout.Token);
        await clientChannel2.Writer.WriteAsync(new MailboxStreamClientMessage
        {
            AuthResponse = new AuthResponseProto
            {
                RoutingToken = ByteString.CopyFrom(routingToken),
                ChallengeHmac = ByteString.CopyFrom(hmac.ComputeHash(challengeMsg2.AuthChallenge.Nonce.ToByteArray()))
            }
        });
        var authResultMsg2 = await serverWriter2.ReadNextAsync(stepTimeout.Token);
        authResultMsg2.AuthResult.Authenticated.Should().BeTrue();

        var replayed = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < floodCount; i++)
        {
            var msg = await serverWriter2.ReadNextAsync(stepTimeout.Token);
            replayed.Add(msg.Envelope.QueueId).Should()
                .BeTrue($"queue id {msg.Envelope.QueueId} must not be delivered twice");
        }
        replayed.Should().HaveCount(floodCount);

        clientChannel2.Writer.Complete();
        await streamTask2;
    }

    [Test]
    public async Task ConnectMailboxStream_MalformedAcks_DoNotDeleteEnvelopes()
    {
        var capabilityStore = new FakeRelayMailboxCapabilityStore();
        var preKeyRepo = new InMemoryPreKeyRepo();
        var cryptoEngine = new SignalCryptoEngine(new CryptographyOptions());
        var endpoint = new AuthenticatedRelayEndpoint(capabilityStore, preKeyRepo, cryptoEngine);

        var routingToken = new byte[16];
        var deliveryToken = new byte[32];
        var drainToken = new byte[32];
        RandomNumberGenerator.Fill(routingToken);
        RandomNumberGenerator.Fill(deliveryToken);
        RandomNumberGenerator.Fill(drainToken);
        await capabilityStore.RegisterAsync(routingToken, deliveryToken, drainToken);

        var clientChannel = Channel.CreateUnbounded<MailboxStreamClientMessage>();
        var serverWriter = new TestServerStreamWriter<MailboxStreamServerMessage>();
        var serverContext = new TestServerCallContext();
        var reader = new ChannelAsyncStreamReader<MailboxStreamClientMessage>(clientChannel.Reader);
        var streamTask = Task.Run(async () =>
            await endpoint.ConnectMailboxStream(reader, serverWriter, serverContext));

        using var stepTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var challengeMsg = await serverWriter.ReadNextAsync(stepTimeout.Token);
        using var hmac = new HMACSHA256(drainToken);
        await clientChannel.Writer.WriteAsync(new MailboxStreamClientMessage
        {
            AuthResponse = new AuthResponseProto
            {
                RoutingToken = ByteString.CopyFrom(routingToken),
                ChallengeHmac = ByteString.CopyFrom(hmac.ComputeHash(challengeMsg.AuthChallenge.Nonce.ToByteArray()))
            }
        });
        var authResultMsg = await serverWriter.ReadNextAsync(stepTimeout.Token);
        authResultMsg.AuthResult.Authenticated.Should().BeTrue();

        await capabilityStore.EnqueueEnvelopeAsync(routingToken, new QueuedSealedEnvelopeProto
        {
            QueueId = "q-ack-target",
            EnvelopePayload = ByteString.CopyFrom(new byte[] { 9 }),
            EnqueuedAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow)
        });
        var liveMsg = await serverWriter.ReadNextAsync(stepTimeout.Token);
        liveMsg.Envelope.QueueId.Should().Be("q-ack-target");

        // Negative ack and empty-queue-id ack are both ignored.
        await clientChannel.Writer.WriteAsync(new MailboxStreamClientMessage
        {
            Ack = new MailboxAckProto { QueueId = "q-ack-target", Acknowledged = false }
        });
        await clientChannel.Writer.WriteAsync(new MailboxStreamClientMessage
        {
            Ack = new MailboxAckProto { QueueId = "", Acknowledged = true }
        });

        // Give the ack loop a beat, then confirm the envelope survived.
        await Task.Delay(200);
        (await capabilityStore.GetEnvelopesAsync(routingToken))
            .Should().ContainSingle(e => e.QueueId == "q-ack-target");

        // A well-formed ack still removes it.
        await clientChannel.Writer.WriteAsync(new MailboxStreamClientMessage
        {
            Ack = new MailboxAckProto { QueueId = "q-ack-target", Acknowledged = true }
        });
        using var ackTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while ((await capabilityStore.GetEnvelopesAsync(routingToken)).Count > 0 && !ackTimeout.IsCancellationRequested)
        {
            await Task.Yield();
        }
        (await capabilityStore.GetEnvelopesAsync(routingToken)).Should().BeEmpty();

        clientChannel.Writer.Complete();
        await streamTask;
    }

    [Test]
    public async Task PublishPreKeyBundle_DuplicateKeyIdsWithinBatch_CollapsedToUnique()
    {
        var capabilityStore = new FakeRelayMailboxCapabilityStore();
        var preKeyRepo = new InMemoryPreKeyRepo();
        var cryptoEngine = new SignalCryptoEngine(new CryptographyOptions());
        var endpoint = new AuthenticatedRelayEndpoint(capabilityStore, preKeyRepo, cryptoEngine);

        var routingToken = new byte[16];
        var deliveryToken = new byte[32];
        var drainToken = new byte[32];
        RandomNumberGenerator.Fill(routingToken);
        RandomNumberGenerator.Fill(deliveryToken);
        RandomNumberGenerator.Fill(drainToken);
        await capabilityStore.RegisterAsync(routingToken, deliveryToken, drainToken);

        byte[] privId = new byte[32];
        byte[] pubIdBytes = new byte[32];
        SignalCrypto.GenerateEd25519KeyPair(privId, pubIdBytes);
        var pubId = IdentityKey.FromSpan(pubIdBytes);

        var (_, pubSigned) = cryptoEngine.GenerateEphemeralKeyPair();
        var prekeySig = cryptoEngine.SignEd25519(privId, pubSigned.Span);

        // Malformed batch: KeyId 7 appears twice.
        var opks = new List<(uint, DhPublicKey)>
        {
            (7u, cryptoEngine.GenerateEphemeralKeyPair().PublicKey),
            (7u, cryptoEngine.GenerateEphemeralKeyPair().PublicKey),
            (8u, cryptoEngine.GenerateEphemeralKeyPair().PublicKey)
        };

        var request = BuildPublishRequest(
            routingToken, drainToken, 1u, pubId, pubSigned, prekeySig, opks);
        var response = await endpoint.PublishPreKeyBundle(request, new TestServerCallContext());
        response.Success.Should().BeTrue();

        var stored = await preKeyRepo.GetAsync(BlindedRoutingToken.FromBytes(routingToken), DeviceId.Primary);
        stored!.AvailableOneTimePreKeyCount.Should().Be(2,
            "duplicate KeyIds inside one batch collapse to a single hosted key");

        // The surviving id-7 key is the FIRST occurrence — the same id served to a
        // second fetcher must never be a consumed-then-republished duplicate.
        stored.OneTimePreKeys.Select(k => k.KeyId).Should().BeEquivalentTo(new uint[] { 7u, 8u });
    }

    /// <summary>
    /// Wraps a capability store and holds <see cref="IRelayMailboxCapabilityStore.GetEnvelopesAsync"/>
    /// until <see cref="Release"/> — lets a test enqueue inside the initial-snapshot window
    /// after the stream's live subscription is already registered.
    /// </summary>
    private sealed class GatedSnapshotCapabilityStore : IRelayMailboxCapabilityStore
    {
        private readonly IRelayMailboxCapabilityStore _inner;
        private TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public GatedSnapshotCapabilityStore(IRelayMailboxCapabilityStore inner) => _inner = inner;

        public void Arm() => _gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Release() => _gate.TrySetResult();

        public async ValueTask<IReadOnlyList<QueuedSealedEnvelopeProto>> GetEnvelopesAsync(
            ReadOnlyMemory<byte> routingToken, CancellationToken ct = default)
        {
            await _gate.Task.WaitAsync(ct);
            return await _inner.GetEnvelopesAsync(routingToken, ct);
        }

        public ValueTask RegisterAsync(ReadOnlyMemory<byte> routingToken, ReadOnlyMemory<byte> deliveryToken, ReadOnlyMemory<byte> drainToken, CancellationToken ct = default)
            => _inner.RegisterAsync(routingToken, deliveryToken, drainToken, ct);

        public ValueTask<DomainResult> RegisterVerifiedAsync(ReadOnlyMemory<byte> routingToken, ReadOnlyMemory<byte> deliveryToken, ReadOnlyMemory<byte> drainToken, ReadOnlyMemory<byte> existingDrainToken = default, CancellationToken ct = default)
            => _inner.RegisterVerifiedAsync(routingToken, deliveryToken, drainToken, existingDrainToken, ct);

        public ValueTask<bool> VerifyDeliveryTokenAsync(ReadOnlyMemory<byte> routingToken, ReadOnlyMemory<byte> deliveryToken, CancellationToken ct = default)
            => _inner.VerifyDeliveryTokenAsync(routingToken, deliveryToken, ct);

        public ValueTask<bool> VerifyDrainTokenAsync(ReadOnlyMemory<byte> routingToken, ReadOnlyMemory<byte> drainToken, CancellationToken ct = default)
            => _inner.VerifyDrainTokenAsync(routingToken, drainToken, ct);

        public ValueTask<ReadOnlyMemory<byte>?> GetDrainTokenAsync(ReadOnlyMemory<byte> routingToken, CancellationToken ct = default)
            => _inner.GetDrainTokenAsync(routingToken, ct);

        public ValueTask EnqueueEnvelopeAsync(ReadOnlyMemory<byte> routingToken, QueuedSealedEnvelopeProto envelope, CancellationToken ct = default)
            => _inner.EnqueueEnvelopeAsync(routingToken, envelope, ct);

        public ValueTask<IReadOnlyList<QueuedSealedEnvelopeProto>> DrainEnvelopesAsync(ReadOnlyMemory<byte> routingToken, CancellationToken ct = default)
            => _inner.DrainEnvelopesAsync(routingToken, ct);

        public IDisposable SubscribeMailbox(ReadOnlyMemory<byte> routingToken, ChannelWriter<QueuedSealedEnvelopeProto> writer)
            => _inner.SubscribeMailbox(routingToken, writer);

        public ValueTask<bool> AcknowledgeEnvelopeAsync(ReadOnlyMemory<byte> routingToken, string queueId, CancellationToken ct = default)
            => _inner.AcknowledgeEnvelopeAsync(routingToken, queueId, ct);
    }

    private sealed class ChannelAsyncStreamReader<T> : IAsyncStreamReader<T>
    {
        private readonly ChannelReader<T> _reader;
        private T? _current;

        public ChannelAsyncStreamReader(ChannelReader<T> reader)
        {
            _reader = reader;
        }

        public T Current => _current ?? throw new InvalidOperationException("No current element.");

        public async Task<bool> MoveNext(CancellationToken cancellationToken)
        {
            while (await _reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
            {
                if (_reader.TryRead(out _current))
                {
                    return true;
                }
            }
            return false;
        }
    }
}

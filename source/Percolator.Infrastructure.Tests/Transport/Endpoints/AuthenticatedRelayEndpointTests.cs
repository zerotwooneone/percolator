namespace Percolator.Infrastructure.Tests.Transport.Endpoints;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using NUnit.Framework;
using Percolator.Domain.Delivery.Hosting;
using Percolator.Domain.Delivery.Ports;
using Percolator.Domain.Identities.ValueObjects;
using Percolator.Domain.Security.ValueObjects;
using Percolator.Infrastructure.Protos;
using Percolator.Infrastructure.Security;
using Percolator.Infrastructure.Transport;
using Percolator.Infrastructure.Transport.Endpoints;
using Signal.Interop;

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

        byte[] privId = new byte[32];
        byte[] pubIdBytes = new byte[32];
        SignalCrypto.GenerateEd25519KeyPair(privId, pubIdBytes);
        var pubId = IdentityKey.FromSpan(pubIdBytes);

        var (_, pubSigned) = cryptoEngine.GenerateEphemeralKeyPair();
        var prekeySig = cryptoEngine.SignEd25519(privId, pubSigned.Span);

        var ownerId = PublicIdentityId.New();
        var deviceId = 1u;
        var now = DateTimeOffset.UtcNow;

        // 1. Initial valid bundle publication at T=now
        var payload1 = new PreKeyBundlePayloadProto
        {
            OwnerIdentityId = ByteString.CopyFrom(ownerId.ToByteArray()),
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
            OwnerIdentityId = ByteString.CopyFrom(ownerId.ToByteArray()),
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
            OwnerIdentityId = ByteString.CopyFrom(ownerId.ToByteArray()),
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

    [Test]
    public async Task RegisterMailbox_ExistingRoutingTokenWithWrongDrainToken_ThrowsPermissionDenied()
    {
        var capabilityStore = new InMemoryRelayMailboxCapabilityStore();
        var preKeyRepo = new InMemoryPreKeyRepo();
        var cryptoEngine = new SignalCryptoEngine();
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
        var capabilityStore = new InMemoryRelayMailboxCapabilityStore();
        var preKeyRepo = new InMemoryPreKeyRepo();
        var cryptoEngine = new SignalCryptoEngine();
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
        var capabilityStore = new InMemoryRelayMailboxCapabilityStore();
        var preKeyRepo = new InMemoryPreKeyRepo();
        var cryptoEngine = new SignalCryptoEngine();
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
        capabilityStore.TotalEnvelopeCount.Should().Be(1);

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
        while (capabilityStore.TotalEnvelopeCount > 0 && !ackTimeout.IsCancellationRequested)
        {
            await Task.Yield();
        }

        capabilityStore.TotalEnvelopeCount.Should().Be(0);

        // 7. Complete client stream to shut down cleanly
        clientChannel.Writer.Complete();
        await streamTask;
    }

    [Test]
    public async Task ConnectMailboxStream_ClientDisconnectsWithoutAck_RetainsOfflineEnvelopesInStore()
    {
        var capabilityStore = new InMemoryRelayMailboxCapabilityStore();
        var preKeyRepo = new InMemoryPreKeyRepo();
        var cryptoEngine = new SignalCryptoEngine();
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
        capabilityStore.TotalEnvelopeCount.Should().Be(1);

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
        capabilityStore.TotalEnvelopeCount.Should().Be(1);
        var stored = await capabilityStore.GetEnvelopesAsync(routingToken);
        stored.Should().ContainSingle(e => e.QueueId == "q-offline-01");
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

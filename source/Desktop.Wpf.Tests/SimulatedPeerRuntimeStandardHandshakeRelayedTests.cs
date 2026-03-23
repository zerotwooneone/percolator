using System;
using System.Linq;
using System.Runtime.Serialization;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Desktop.Wpf.Features.Simulator;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NUnit.Framework;
using Percolator.Application.Configuration;
using Percolator.Contracts;
using Percolator.Cryptography;
using Percolator.Cryptography.Primitives;

namespace Desktop.Wpf.Tests;

[TestFixture]
public sealed class SimulatedPeerRuntimeStandardHandshakeRelayedTests
{
    private sealed class InMemoryRepository : ISimulatorStateRepository
    {
        public SimulatorStateDto? State { get; set; }

        public Task<SimulatorStateDto?> LoadAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(State);

        public Task SaveAsync(SimulatorStateDto state, CancellationToken cancellationToken = default)
        {
            State = state;
            return Task.CompletedTask;
        }
    }

    private static SimulatorStateService CreateSut(
        InMemoryRepository repo,
        SimulatorDiagnosticsService diagnostics,
        ISimulatedPeerPendingInbox pending,
        IClock clock)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IClock>(clock);

        var sp = services.BuildServiceProvider();
        var scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();

        var keys = new SimulatedPeerKeyFactory();
        var options = Options.Create(new TransportOptions { GrpcPort = 5002 });
        var engine = new Desktop.Wpf.Features.Simulator.Protocol.SignalProtocolEngine(new TestClock(TestClock.Default));
        return new SimulatorStateService(repo, keys, options, diagnostics, pending, scopeFactory, engine);
    }

    [Test]
    public async Task AcceptReverseSignalInviteAsync_persists_runtime_store()
    {
        var simulatedPeerId = Guid.NewGuid();
        var inviterPeerId = Guid.NewGuid();

        using var acceptorIdentityEcdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var acceptorIdentityPriv = acceptorIdentityEcdh.ExportECPrivateKey();
        using var acceptorIdentityEcdsa = ECDsa.Create(acceptorIdentityEcdh.ExportParameters(true));
        var acceptorIdentitySpki = acceptorIdentityEcdsa.ExportSubjectPublicKeyInfo();

        using var inviterIdentityEcdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var inviterIdentityPriv = inviterIdentityEcdh.ExportECPrivateKey();
        using var inviterIdentityEcdsa = ECDsa.Create(inviterIdentityEcdh.ExportParameters(true));
        var inviterIdentitySpki = inviterIdentityEcdsa.ExportSubjectPublicKeyInfo();

        using var inviterSignedPreKeyEcdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var inviterSignedPreKeySpki = inviterSignedPreKeyEcdh.PublicKey.ExportSubjectPublicKeyInfo();
        var preKeySig = inviterIdentityEcdsa.SignData(inviterSignedPreKeySpki, HashAlgorithmName.SHA256);

        var payload = new InviteHandshakeRequestPayload
        {
            Version = 1,
            RequestCorrelationId = Guid.NewGuid().ToString(),
            InviterPreKey = new InviteHandshakePreKeyBundle
            {
                Version = 1,
                InviterSignedPreKey = ByteString.CopyFrom(inviterSignedPreKeySpki),
                PreKeySignature = ByteString.CopyFrom(preKeySig)
            }
        };

        var invite = new EstablishDirectSessionRequest
        {
            Version = 1,
            InviterIdentityKey = ByteString.CopyFrom(inviterIdentitySpki),
            Payload = ByteString.CopyFrom(payload.ToByteArray())
        };

        var repo = new InMemoryRepository
        {
            State = new SimulatorStateDto
            {
                Version = 1,
                Peers =
                {
                    new SimulatedPeerDto
                    {
                        PeerId = simulatedPeerId,
                        DisplayName = "sim",
                        IsOnline = true,
                        Relay = new SimulatedPeerRelayStateDto { IsRelayCapable = false },
                        ReverseSignalKeys = new SimulatedPeerReverseSignalKeysDto
                        {
                            IdentitySigningKeySpki = acceptorIdentitySpki,
                            IdentitySigningKeyPrivateKeyEcPrivateKey = acceptorIdentityPriv
                        }
                    }
                }
            }
        };

        var pending = new SimulatedPeerPendingInbox();
        var diagnostics = new SimulatorDiagnosticsService();
        var clock = new StaticClock(StaticClock.DefaultNow);
        var sut = CreateSut(repo, diagnostics, pending, clock);
        await sut.InitializeAsync(CancellationToken.None);

        var acceptance = await sut.AcceptReverseSignalInviteAsync(simulatedPeerId, inviterPeerId, invite, CancellationToken.None);

        acceptance.SessionId.Should().NotBeNull();
        acceptance.Response.Should().NotBeNull();
        acceptance.Response.Version.Should().Be(1);

        await Task.Delay(300);
        repo.State!.Peers.Should().ContainSingle(p => p.PeerId == simulatedPeerId);
        repo.State!.Peers.Single(p => p.PeerId == simulatedPeerId).RuntimeStore.Sessions.Should().HaveCount(1);
    }

    [Test]
    public async Task Relayed_HandshakeInitiatorHello_is_handled_and_persists_runtime_store()
    {
        var simulatedPeerId = Guid.NewGuid();

        using var responderIdentityEcdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var responderIdentityPriv = responderIdentityEcdh.ExportECPrivateKey();
        using var responderIdentityEcdsa = ECDsa.Create(responderIdentityEcdh.ExportParameters(true));
        var responderIdentitySpki = responderIdentityEcdsa.ExportSubjectPublicKeyInfo();

        var repo = new InMemoryRepository
        {
            State = new SimulatorStateDto
            {
                Version = 1,
                Peers =
                {
                    new SimulatedPeerDto
                    {
                        PeerId = simulatedPeerId,
                        DisplayName = "sim",
                        IsOnline = true,
                        Relay = new SimulatedPeerRelayStateDto { IsRelayCapable = false },
                        ReverseSignalKeys = new SimulatedPeerReverseSignalKeysDto
                        {
                            IdentitySigningKeySpki = responderIdentitySpki,
                            IdentitySigningKeyPrivateKeyEcPrivateKey = responderIdentityPriv
                        }
                    }
                }
            }
        };

        var pending = new SimulatedPeerPendingInbox();
        var diagnostics = new SimulatorDiagnosticsService();
        var clock = new StaticClock(StaticClock.DefaultNow);
        var sut = CreateSut(repo, diagnostics, pending, clock);
        await sut.InitializeAsync(CancellationToken.None);

        using var initiatorIdentityEcdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        using var initiatorIdentityEcdsa = ECDsa.Create(initiatorIdentityEcdh.ExportParameters(true));
        var initiatorIdentitySpki = initiatorIdentityEcdsa.ExportSubjectPublicKeyInfo();

        using var initiatorEph = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var initiatorEphSpki = initiatorEph.PublicKey.ExportSubjectPublicKeyInfo();

        var spkId = Guid.NewGuid();
        var hello = new HandshakeInitiatorHello
        {
            Version = 1,
            InitiatorIdentityKeySpki = ByteString.CopyFrom(initiatorIdentitySpki),
            InitiatorEphemeralKeySpki = ByteString.CopyFrom(initiatorEphSpki),
            SignedPreKeyId = ByteString.CopyFrom(spkId.ToByteArray())
        };

        var resp = await sut.ReceiveRelayedOpaquePayloadAsync(simulatedPeerId, hello.ToByteArray(), CancellationToken.None);

        resp.Should().NotBeNull();
        resp!.Version.Should().Be(1);
        resp.Response.Should().NotBeNull();
        resp.Response.ResponsePayload.Should().NotBeNull();

        await Task.Delay(300);
        repo.State!.Peers.Single(p => p.PeerId == simulatedPeerId).RuntimeStore.Sessions.Should().HaveCount(1);
    }

    [Test]
    public async Task When_initiating_standard_handshake_via_relay_it_enqueues_handshake_initiator_hello_to_relay_host()
    {
        // Arrange
        var simulatedPeerId = Guid.NewGuid();
        var relayHostPeerId = Guid.NewGuid();

        using var initiatorIdentityEcdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var initiatorIdentityPriv = initiatorIdentityEcdh.ExportECPrivateKey();
        using var initiatorIdentityEcdsa = ECDsa.Create(initiatorIdentityEcdh.ExportParameters(true));
        var initiatorIdentitySpki = initiatorIdentityEcdsa.ExportSubjectPublicKeyInfo();

        var pending = new SimulatedPeerPendingInbox();

        var bundle = CreateValidResponderPreKeyBundle();
        var responderPkh = bundle.ResponderPkh;
        var preKeyBundleBytes = bundle.BundleBytes;

        var repo = new InMemoryRepository
        {
            State = new SimulatorStateDto
            {
                Version = 1,
                Peers =
                {
                    new SimulatedPeerDto
                    {
                        PeerId = simulatedPeerId,
                        DisplayName = "sim",
                        IsOnline = true,
                        Relay = new SimulatedPeerRelayStateDto { IsRelayCapable = false },
                        ReverseSignalKeys = new SimulatedPeerReverseSignalKeysDto
                        {
                            IdentitySigningKeySpki = initiatorIdentitySpki,
                            IdentitySigningKeyPrivateKeyEcPrivateKey = initiatorIdentityPriv
                        }
                    },
                    new SimulatedPeerDto
                    {
                        PeerId = relayHostPeerId,
                        DisplayName = "relay",
                        IsOnline = true,
                        Relay = new SimulatedPeerRelayStateDto
                        {
                            IsRelayCapable = true,
                            PreKeyStore = new SimulatedRelayPreKeyStoreDto
                            {
                                Version = 1,
                                PublishedBundles =
                                {
                                    new PublishedPreKeyBundleDto
                                    {
                                        RecipientPublicKeyHash = responderPkh,
                                        LogicalOwnerPeerId = Guid.NewGuid(),
                                        BundleBytes = preKeyBundleBytes,
                                        ExpiresUtc = DateTimeOffset.UtcNow.AddMinutes(5)
                                    }
                                }
                            }
                        },
                        // ReverseSignalKeys will be normalized/ensured by the service.
                    }
                }
            }
        };

        var diagnostics = new SimulatorDiagnosticsService();
        var clock = new StaticClock(StaticClock.DefaultNow);
        var sut = CreateSut(repo, diagnostics, pending, clock);
        await sut.InitializeAsync(CancellationToken.None);

        // Act
        var sid = await sut.InitiateStandardHandshakeToMainByRelayPkhAsync(
            simulatedPeerId,
            relayHostPeerId,
            responderPkh,
            CancellationToken.None);

        // Assert
        sid.Should().BeNull();

        var queued = repo.State!.Peers.Single(p => p.PeerId == relayHostPeerId)
            .Relay.OpaqueQueue.Items
            .Single(i => i.DebugType == nameof(HandshakeInitiatorHello));
        queued.RecipientRoutingKey.Should().Equal(responderPkh);

        diagnostics.Events.Should().Contain(e =>
            e.EventType == SimulatorDiagnosticEventType.PreKeyBundleFetched
            && e.RelayHostPeerId == relayHostPeerId);
        diagnostics.Events.Should().Contain(e =>
            e.EventType == SimulatorDiagnosticEventType.StandardHandshakeHelloEnqueued
            && e.PeerId == simulatedPeerId
            && e.RelayHostPeerId == relayHostPeerId);
    }

    [Test]
    public async Task When_relay_returns_bundle_with_mismatched_identity_key_it_does_not_enqueue_handshake_hello()
    {
        // Arrange
        var simulatedPeerId = Guid.NewGuid();
        var relayHostPeerId = Guid.NewGuid();

        using var initiatorIdentityEcdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var initiatorIdentityPriv = initiatorIdentityEcdh.ExportECPrivateKey();
        using var initiatorIdentityEcdsa = ECDsa.Create(initiatorIdentityEcdh.ExportParameters(true));
        var initiatorIdentitySpki = initiatorIdentityEcdsa.ExportSubjectPublicKeyInfo();

        var pending = new SimulatedPeerPendingInbox();

        var requestedResponderPkh = SHA256.HashData(Guid.NewGuid().ToByteArray());

        // Bundle is valid, but its identity key hashes to a different PKH.
        var bundleBytes = CreateValidResponderPreKeyBundle().BundleBytes;

        var repo = new InMemoryRepository
        {
            State = new SimulatorStateDto
            {
                Version = 1,
                Peers =
                {
                    new SimulatedPeerDto
                    {
                        PeerId = simulatedPeerId,
                        DisplayName = "sim",
                        IsOnline = true,
                        Relay = new SimulatedPeerRelayStateDto { IsRelayCapable = false },
                        ReverseSignalKeys = new SimulatedPeerReverseSignalKeysDto
                        {
                            IdentitySigningKeySpki = initiatorIdentitySpki,
                            IdentitySigningKeyPrivateKeyEcPrivateKey = initiatorIdentityPriv
                        }
                    },
                    new SimulatedPeerDto
                    {
                        PeerId = relayHostPeerId,
                        DisplayName = "relay",
                        IsOnline = true,
                        Relay = new SimulatedPeerRelayStateDto
                        {
                            IsRelayCapable = true,
                            PreKeyStore = new SimulatedRelayPreKeyStoreDto
                            {
                                Version = 1,
                                PublishedBundles =
                                {
                                    new PublishedPreKeyBundleDto
                                    {
                                        RecipientPublicKeyHash = requestedResponderPkh,
                                        LogicalOwnerPeerId = Guid.NewGuid(),
                                        BundleBytes = bundleBytes,
                                        ExpiresUtc = DateTimeOffset.UtcNow.AddMinutes(5)
                                    }
                                }
                            }
                        },
                        ReverseSignalKeys = new SimulatedPeerReverseSignalKeysDto
                        {
                            IdentitySigningKeySpki = SHA256.HashData(Guid.NewGuid().ToByteArray()),
                            IdentitySigningKeyPrivateKeyEcPrivateKey = new byte[] { 0x01 }
                        }
                    }
                }
            }
        };

        var diagnostics = new SimulatorDiagnosticsService();
        var clock = new StaticClock(StaticClock.DefaultNow);
        var sut = CreateSut(repo, diagnostics, pending, clock);
        await sut.InitializeAsync(CancellationToken.None);

        // Act
        var sid = await sut.InitiateStandardHandshakeToMainByRelayPkhAsync(
            simulatedPeerId,
            relayHostPeerId,
            requestedResponderPkh,
            CancellationToken.None);

        // Assert
        sid.Should().BeNull();

        repo.State!.Peers.Single(p => p.PeerId == relayHostPeerId).Relay.OpaqueQueue.Items.Should().BeEmpty();

        diagnostics.Events.Should().NotContain(e => e.EventType == SimulatorDiagnosticEventType.StandardHandshakeHelloEnqueued);
    }

    private sealed record ResponderBundleFixture(byte[] BundleBytes, byte[] ResponderPkh);

    private static ResponderBundleFixture CreateValidResponderPreKeyBundle()
    {
        using var responderIdentityEcdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        using var responderIdentityEcdsa = ECDsa.Create(responderIdentityEcdh.ExportParameters(true));
        var responderIdentitySpki = responderIdentityEcdsa.ExportSubjectPublicKeyInfo();

        using var responderSignedPreKey = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var responderSignedPreKeySpki = responderSignedPreKey.PublicKey.ExportSubjectPublicKeyInfo();
        var preKeySig = responderIdentityEcdsa.SignData(responderSignedPreKeySpki, HashAlgorithmName.SHA256);

        var bundle = new GetPreKeyBundleResponse.Types.PreKeyBundle
        {
            Version = 1,
            IdentityKey = ByteString.CopyFrom(responderIdentitySpki),
            SignedPreKeyId = ByteString.CopyFrom(Guid.NewGuid().ToByteArray()),
            SignedPreKey = ByteString.CopyFrom(responderSignedPreKeySpki),
            PreKeySignature = ByteString.CopyFrom(preKeySig)
        };

        var bytes = bundle.ToByteArray();
        var pkh = SHA256.HashData(responderIdentitySpki);
        return new ResponderBundleFixture(bytes, pkh);
    }

    [Test]
    public async Task RelayHost_Returns_ResponsePayload_for_GetPreKeyBundleRequest_when_bundle_found()
    {
        // Arrange
        var relayHostPeerId = Guid.NewGuid();

        using var relayIdentityEcdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var relayIdentityPriv = relayIdentityEcdh.ExportECPrivateKey();
        using var relayIdentityEcdsa = ECDsa.Create(relayIdentityEcdh.ExportParameters(true));
        var relayIdentitySpki = relayIdentityEcdsa.ExportSubjectPublicKeyInfo();
        var pending = new SimulatedPeerPendingInbox();

        var bundle = CreateValidResponderPreKeyBundle();

        var clock = new StaticClock(StaticClock.DefaultNow);
        var sessionId = SessionId.NewId();
        var root = new RootKey(new byte[32]);
        var initiatorSession = RatchetBootstrap.CreateInitiatorSession(
            sessionId,
            PeerId.NewId(),
            new ProtocolVersion(1),
            root,
            clock);
        var responderSession = RatchetBootstrap.CreateResponderSession(
            sessionId,
            PeerId.NewId(),
            new ProtocolVersion(1),
            root,
            clock);

        var seededStore = new SimulatedPeerRuntimeStoreDto
        {
            Version = 1,
            Sessions =
            {
                new SimulatedSecureSessionDto
                {
                    SessionId = responderSession.Id.Value,
                    RemotePeerId = responderSession.RemotePeerId.Value,
                    ProtocolVersion = responderSession.ProtocolVersion.Value,
                    RootKey = responderSession.State.RootKey.Value,
                    SendChainKey = responderSession.State.SendingChainKey?.Value,
                    SendCounter = responderSession.State.SendingCounter,
                    RecvChainKey = responderSession.State.ReceivingChainKey?.Value,
                    RecvCounter = responderSession.State.ReceivingCounter,
                    PrevChainLength = responderSession.State.PreviousChainLength,
                    RemoteRatchetKey = responderSession.State.RemoteRatchetKey?.Value,
                    DhRatchetPrivateKey = responderSession.State.DhRatchetPrivateKey?.Value,
                    SkippedKeysCount = responderSession.SkippedKeysCount,
                    CreatedAtUtc = responderSession.CreatedAtUtc,
                    LastUsedAtUtc = responderSession.LastUsedAtUtc
                }
            }
        };

        var repo = new InMemoryRepository
        {
            State = new SimulatorStateDto
            {
                Version = 1,
                Peers =
                {
                    new SimulatedPeerDto
                    {
                        PeerId = relayHostPeerId,
                        DisplayName = "relay",
                        IsOnline = true,
                        Relay = new SimulatedPeerRelayStateDto
                        {
                            IsRelayCapable = true,
                            PreKeyStore = new SimulatedRelayPreKeyStoreDto
                            {
                                Version = 1,
                                PublishedBundles =
                                {
                                    new PublishedPreKeyBundleDto
                                    {
                                        RecipientPublicKeyHash = bundle.ResponderPkh,
                                        LogicalOwnerPeerId = Guid.NewGuid(),
                                        BundleBytes = bundle.BundleBytes,
                                        ExpiresUtc = DateTimeOffset.UtcNow.AddMinutes(5)
                                    }
                                }
                            }
                        },
                        ReverseSignalKeys = new SimulatedPeerReverseSignalKeysDto
                        {
                            IdentitySigningKeySpki = relayIdentitySpki,
                            IdentitySigningKeyPrivateKeyEcPrivateKey = relayIdentityPriv
                        },
                        RuntimeStore = seededStore
                    }
                }
            }
        };

        var diagnostics = new SimulatorDiagnosticsService();
        var sut = CreateSut(repo, diagnostics, pending, clock);
        await sut.InitializeAsync(CancellationToken.None);

        var envReq = new InternalEnvelope
        {
            PrekeyEnvelope = new PrekeyEnvelope
            {
                Version = 1,
                GetPreKeyBundleRequest = new GetPreKeyBundleRequest
                {
                    Version = 1,
                    PublicKeyHash = ByteString.CopyFrom(bundle.ResponderPkh)
                }
            }
        };

        var pt = new Plaintext(envReq.ToByteArray());
        var cipher = initiatorSession.Encrypt(pt, clock);
        var deliverReq = new DeliverOpaqueMessageRequest { Version = 1, Payload = ByteString.CopyFrom(cipher.Value) };

        // Act
        var deliverResp = await sut.ReceiveOpaqueMessageFromMainAsync(relayHostPeerId, deliverReq, CancellationToken.None);

        // Assert
        deliverResp.ResultCase.Should().Be(DeliverOpaqueMessageResponse.ResultOneofCase.ResponsePayload);
        deliverResp.ResponsePayload.Should().NotBeNull();
        deliverResp.ResponsePayload!.ResponsePayload.Length.Should().BeGreaterThan(0);

        var respCipher = new SessionRatchetMessage(deliverResp.ResponsePayload.ResponsePayload.ToByteArray());
        var respPlain = initiatorSession.Decrypt(respCipher, clock);
        var respEnv = InternalEnvelope.Parser.ParseFrom(respPlain.Value);
        respEnv.ApplicationPayloadCase.Should().Be(InternalEnvelope.ApplicationPayloadOneofCase.GetPreKeyBundleResponse);
        respEnv.GetPreKeyBundleResponse.Should().NotBeNull();
        respEnv.GetPreKeyBundleResponse.PreKeyBundle.Should().NotBeNull();
    }

    [Test]
    public async Task RelayHost_Returns_ResponsePayload_for_GetPreKeyBundleRequest_when_bundle_not_found()
    {
        // Arrange
        var relayHostPeerId = Guid.NewGuid();

        using var relayIdentityEcdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var relayIdentityPriv = relayIdentityEcdh.ExportECPrivateKey();
        using var relayIdentityEcdsa = ECDsa.Create(relayIdentityEcdh.ExportParameters(true));
        var relayIdentitySpki = relayIdentityEcdsa.ExportSubjectPublicKeyInfo();
        var pending = new SimulatedPeerPendingInbox();

        var requestedPkh = SHA256.HashData(Guid.NewGuid().ToByteArray());

        var clock = new StaticClock(StaticClock.DefaultNow);
        var sessionId = SessionId.NewId();
        var root = new RootKey(new byte[32]);
        var initiatorSession = RatchetBootstrap.CreateInitiatorSession(
            sessionId,
            PeerId.NewId(),
            new ProtocolVersion(1),
            root,
            clock);
        var responderSession = RatchetBootstrap.CreateResponderSession(
            sessionId,
            PeerId.NewId(),
            new ProtocolVersion(1),
            root,
            clock);

        var seededStore = new SimulatedPeerRuntimeStoreDto
        {
            Version = 1,
            Sessions =
            {
                new SimulatedSecureSessionDto
                {
                    SessionId = responderSession.Id.Value,
                    RemotePeerId = responderSession.RemotePeerId.Value,
                    ProtocolVersion = responderSession.ProtocolVersion.Value,
                    RootKey = responderSession.State.RootKey.Value,
                    SendChainKey = responderSession.State.SendingChainKey?.Value,
                    SendCounter = responderSession.State.SendingCounter,
                    RecvChainKey = responderSession.State.ReceivingChainKey?.Value,
                    RecvCounter = responderSession.State.ReceivingCounter,
                    PrevChainLength = responderSession.State.PreviousChainLength,
                    RemoteRatchetKey = responderSession.State.RemoteRatchetKey?.Value,
                    DhRatchetPrivateKey = responderSession.State.DhRatchetPrivateKey?.Value,
                    SkippedKeysCount = responderSession.SkippedKeysCount,
                    CreatedAtUtc = responderSession.CreatedAtUtc,
                    LastUsedAtUtc = responderSession.LastUsedAtUtc
                }
            }
        };

        var repo = new InMemoryRepository
        {
            State = new SimulatorStateDto
            {
                Version = 1,
                Peers =
                {
                    new SimulatedPeerDto
                    {
                        PeerId = relayHostPeerId,
                        DisplayName = "relay",
                        IsOnline = true,
                        Relay = new SimulatedPeerRelayStateDto { IsRelayCapable = true },
                        ReverseSignalKeys = new SimulatedPeerReverseSignalKeysDto
                        {
                            IdentitySigningKeySpki = relayIdentitySpki,
                            IdentitySigningKeyPrivateKeyEcPrivateKey = relayIdentityPriv
                        },
                        RuntimeStore = seededStore
                    }
                }
            }
        };

        var diagnostics = new SimulatorDiagnosticsService();
        var sut = CreateSut(repo, diagnostics, pending, clock);
        await sut.InitializeAsync(CancellationToken.None);

        var envReq = new InternalEnvelope
        {
            PrekeyEnvelope = new PrekeyEnvelope
            {
                Version = 1,
                GetPreKeyBundleRequest = new GetPreKeyBundleRequest
                {
                    Version = 1,
                    PublicKeyHash = ByteString.CopyFrom(requestedPkh)
                }
            }
        };

        var pt = new Plaintext(envReq.ToByteArray());
        var cipher = initiatorSession.Encrypt(pt, clock);
        var deliverReq = new DeliverOpaqueMessageRequest { Version = 1, Payload = ByteString.CopyFrom(cipher.Value) };

        // Act
        var deliverResp = await sut.ReceiveOpaqueMessageFromMainAsync(relayHostPeerId, deliverReq, CancellationToken.None);

        // Assert
        deliverResp.ResultCase.Should().Be(DeliverOpaqueMessageResponse.ResultOneofCase.ResponsePayload);
        deliverResp.ResponsePayload.Should().NotBeNull();
        deliverResp.ResponsePayload!.ResponsePayload.Length.Should().BeGreaterThan(0);

        var respCipher = new SessionRatchetMessage(deliverResp.ResponsePayload.ResponsePayload.ToByteArray());
        var respPlain = initiatorSession.Decrypt(respCipher, clock);
        var respEnv = InternalEnvelope.Parser.ParseFrom(respPlain.Value);
        respEnv.ApplicationPayloadCase.Should().Be(InternalEnvelope.ApplicationPayloadOneofCase.GetPreKeyBundleResponse);
        respEnv.GetPreKeyBundleResponse.Should().NotBeNull();
        respEnv.GetPreKeyBundleResponse.PreKeyBundle.Should().BeNull();
    }
}

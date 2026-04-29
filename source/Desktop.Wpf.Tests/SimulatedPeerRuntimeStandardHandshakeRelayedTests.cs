using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Desktop.Wpf.Features.Simulator;
using Desktop.Wpf.Features.Simulator.Models;
using FluentAssertions;
using Google.Protobuf;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NUnit.Framework;
using ObservableCollections;
using Percolator.Application.Configuration;
using Percolator.Contracts;
using Percolator.Cryptography;
using Percolator.Identity;

namespace Desktop.Wpf.Tests;

[TestFixture]
public sealed class SimulatedPeerRuntimeStandardHandshakeRelayedTests
{
    private sealed class InMemoryRepository : ISimulatorStateRepository
    {
        public IReadOnlyList<SimulatedPeerModel> Peers { get; set; } = Array.Empty<SimulatedPeerModel>();

        public IReadOnlyList<PeerRelationship> Relationships { get; set; } = Array.Empty<PeerRelationship>();

        public IReadOnlyList<SimulatedRelayModel> Relays { get; set; } = Array.Empty<SimulatedRelayModel>();

        public SimulatorStateSnapshot? SavedSnapshot { get; private set; }

        public Task<SimulatorStateSnapshot> LoadStateAsync(CancellationToken cancellationToken = default)
        {
            var peerSnaps = Peers.Select(p => p.Freeze()).ToList();
            var relSnaps = Relationships.Select(r => new PeerRelationshipSnapshot(r.SourcePeerId, r.TargetPeerId, r.Type)).ToList();
            var relaySnaps = Relays.Select(r => r.Freeze()).ToList();

            return Task.FromResult(new SimulatorStateSnapshot(
                Version: 1,
                Peers: peerSnaps,
                Relationships: relSnaps,
                Relays: relaySnaps,
                Groups: Array.Empty<GroupConversationDto>()));
        }

        public Task SaveStateAsync(SimulatorStateSnapshot snapshot, CancellationToken cancellationToken = default)
        {
            SavedSnapshot = snapshot;
            return Task.CompletedTask;
        }
    }

    private static SimulatorStateService CreateSut(
        InMemoryRepository repo,
        SimulatorDiagnosticsService diagnostics,
        ISimulatedPeerPendingInbox pending,
        IClock clock,
        out FakeTimeProvider timeProvider)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IClock>(clock);

        var transportOptions = Options.Create(new TransportOptions { SimulatorPort = 5002 });

        var engine = new Desktop.Wpf.Features.Simulator.Protocol.SignalProtocolEngine(new TestClock(TestClock.Default));
        timeProvider = new FakeTimeProvider();
        return new SimulatorStateService(
            repo,
            diagnostics,
            pending,
            services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
            transportOptions,
            engine,
            timeProvider);
    }

    [Test]
    public async Task AcceptReverseSignalInviteAsync_creates_session_in_memory()
    {
        var simulatedPeerId = new Percolator.Network.PeerId(Guid.NewGuid());
        var inviterPeerId = new Percolator.Network.PeerId(Guid.NewGuid());

        using var acceptorIdentityEcdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var acceptorIdentityPriv = acceptorIdentityEcdh.ExportECPrivateKey();
        using var acceptorIdentityEcdsa = ECDsa.Create(acceptorIdentityEcdh.ExportParameters(true));
        var acceptorIdentitySpki = acceptorIdentityEcdsa.ExportSubjectPublicKeyInfo();

        using var inviterIdentityEcdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
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
            Peers = new[]
            {
                new SimulatedPeerModel(
                    peerId: simulatedPeerId,
                    selfIdentityId: 99000,
                    displayName: "sim",
                    isOnline: true,
                    isRelayCapable: false,
                    identitySigningKeySpki: acceptorIdentitySpki,
                    identitySigningKeyPrivateKeyEcPrivateKey: acceptorIdentityPriv)
            }
        };

        var pending = new SimulatedPeerPendingInbox();
        var diagnostics = new SimulatorDiagnosticsService();
        var clock = new StaticClock(StaticClock.DefaultNow);
        var sut = CreateSut(repo, diagnostics, pending, clock, out var timeProvider);
        await ((ISimulatorStateInitializer)sut).InitializeAsync(CancellationToken.None);

        var acceptance = await sut.AcceptReverseSignalInviteAsync(simulatedPeerId, inviterPeerId, invite, CancellationToken.None);

        acceptance.SessionId.Should().NotBeNull();
        acceptance.Response.Should().NotBeNull();
        acceptance.Response.Version.Should().Be(1);

        sut.Peers.Should().ContainSingle(p => p.PeerId == simulatedPeerId);
        sut.Peers.Single(p => p.PeerId == simulatedPeerId).Sessions.Count.Should().Be(1);
    }

    [Test]
    public async Task AcceptReverseSignalInviteAsync_eventually_persists_runtime_store()
    {
        // Arrange
        var simulatedPeerId = new Percolator.Network.PeerId(Guid.NewGuid());
        var inviterPeerId = new Percolator.Network.PeerId(Guid.NewGuid());

        using var acceptorIdentityEcdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var acceptorIdentityPriv = acceptorIdentityEcdh.ExportECPrivateKey();
        using var acceptorIdentityEcdsa = ECDsa.Create(acceptorIdentityEcdh.ExportParameters(true));
        var acceptorIdentitySpki = acceptorIdentityEcdsa.ExportSubjectPublicKeyInfo();

        using var inviterIdentityEcdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
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
            Peers = new[]
            {
                new SimulatedPeerModel(
                    peerId: simulatedPeerId,
                    selfIdentityId: 99000,
                    displayName: "sim",
                    isOnline: true,
                    isRelayCapable: false,
                    identitySigningKeySpki: acceptorIdentitySpki,
                    identitySigningKeyPrivateKeyEcPrivateKey: acceptorIdentityPriv)
            }
        };

        var pending = new SimulatedPeerPendingInbox();
        var diagnostics = new SimulatorDiagnosticsService();
        var clock = new StaticClock(StaticClock.DefaultNow);
        var sut = CreateSut(repo, diagnostics, pending, clock, out var timeProvider);
        await ((ISimulatorStateInitializer)sut).InitializeAsync(CancellationToken.None);

        // Act
        _ = await sut.AcceptReverseSignalInviteAsync(simulatedPeerId, inviterPeerId, invite, CancellationToken.None);

        // Assert: advance time to trigger debounced save
        timeProvider.Advance(TimeSpan.FromMilliseconds(300));

        repo.SavedSnapshot.Should().NotBeNull();
        repo.SavedSnapshot!.Peers.Single(p => p.PeerId == simulatedPeerId).Sessions.Count.Should().Be(1);
    }

    [Test]
    public async Task Relayed_HandshakeInitiatorHello_is_handled_and_persists_runtime_store()
    {
        var simulatedPeerId = new Percolator.Network.PeerId(Guid.NewGuid());
        var relayHostPeerId = new Percolator.Network.PeerId(Guid.NewGuid());

        using var responderIdentityEcdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var responderIdentityPriv = responderIdentityEcdh.ExportECPrivateKey();
        using var responderIdentityEcdsa = ECDsa.Create(responderIdentityEcdh.ExportParameters(true));
        var responderIdentitySpki = responderIdentityEcdsa.ExportSubjectPublicKeyInfo();

        var spkId = Guid.NewGuid();
        using var responderSignedPreKey = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var responderSignedPreKeySpki = responderSignedPreKey.PublicKey.ExportSubjectPublicKeyInfo();
        var responderSignedPreKeyPriv = responderSignedPreKey.ExportECPrivateKey();

        var repo = new InMemoryRepository
        {
            Peers = new[]
            {
                new SimulatedPeerModel(
                    peerId: simulatedPeerId,
                    selfIdentityId: 99000,
                    displayName: "sim",
                    isOnline: true,
                    isRelayCapable: false,
                    identitySigningKeySpki: responderIdentitySpki,
                    identitySigningKeyPrivateKeyEcPrivateKey: responderIdentityPriv)
            },
            Relays = new[]
            {
                new SimulatedRelayModel(relayHostPeerId)
            }
        };

        // Add signed prekey to responder peer after construction
        repo.Peers.Single(p => p.PeerId == simulatedPeerId)
            .SignedPreKeysMutable.Add(new SimulatedSignedPreKeyModel(spkId, responderSignedPreKeyPriv, responderSignedPreKeySpki));

        var pending = new SimulatedPeerPendingInbox();
        var diagnostics = new SimulatorDiagnosticsService();
        var clock = new StaticClock(StaticClock.DefaultNow);
        var sut = CreateSut(repo, diagnostics, pending, clock, out var timeProvider);
        await ((ISimulatorStateInitializer)sut).InitializeAsync(CancellationToken.None);

        using var initiatorIdentityEcdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        using var initiatorIdentityEcdsa = ECDsa.Create(initiatorIdentityEcdh.ExportParameters(true));
        var initiatorIdentitySpki = initiatorIdentityEcdsa.ExportSubjectPublicKeyInfo();

        using var initiatorEph = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var initiatorEphSpki = initiatorEph.PublicKey.ExportSubjectPublicKeyInfo();

        var hello = new HandshakeInitiatorHello
        {
            Version = 1,
            InitiatorIdentityKeySpki = ByteString.CopyFrom(initiatorIdentitySpki),
            InitiatorEphemeralKeySpki = ByteString.CopyFrom(initiatorEphSpki),
            SignedPreKeyId = ByteString.CopyFrom(spkId.ToByteArray())
        };

        // Act: relay delivery stores standard hello as pending (no immediate response)
        await sut.UpsertPendingStandardSignalHelloAsync(
            recipientPeerId: simulatedPeerId,
            relayHostPeerId: relayHostPeerId,
            hello: hello,
            receivedUtc: StaticClock.DefaultNow,
            cancellationToken: CancellationToken.None);

        // Assert: pending is present and no session yet
        var initiatorPkhHex = Convert.ToHexString(SHA256.HashData(initiatorIdentitySpki)).ToLowerInvariant();
        sut.Peers.Single(p => p.PeerId == simulatedPeerId)
            .PendingInboundStandardSignalHellos
            .ContainsKey(initiatorPkhHex)
            .Should().BeTrue();

        sut.Peers.Single(p => p.PeerId == simulatedPeerId).Sessions.Count.Should().Be(0);

        // Act: user accepts the pending hello, which establishes session and enqueues response back to initiator
        var accepted = await sut.TryAcceptPendingStandardSignalHelloAsync(
            recipientPeerId: simulatedPeerId,
            initiatorPkhHex: initiatorPkhHex,
            cancellationToken: CancellationToken.None);

        accepted.Should().BeTrue();

        var relay = sut.Relays.Single(r => r.RelayHostPeerId == relayHostPeerId);
        relay.MessageQueue.Select(kvp => kvp.Value)
            .OfType<OutboundRelayMessage>()
            .Any(m => m.DebugType == nameof(EstablishSessionResponse))
            .Should().BeTrue();

        sut.Peers.Single(p => p.PeerId == simulatedPeerId).Sessions.Count.Should().Be(1);
    }

    [Test]
    public async Task When_initiating_standard_handshake_via_relay_it_enqueues_handshake_initiator_hello_to_relay_host()
    {
        // Arrange
        var simulatedPeerId = new Percolator.Network.PeerId(Guid.NewGuid());
        var relayHostPeerId = new Percolator.Network.PeerId(Guid.NewGuid());

        using var initiatorIdentityEcdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var initiatorIdentityPriv = initiatorIdentityEcdh.ExportECPrivateKey();
        using var initiatorIdentityEcdsa = ECDsa.Create(initiatorIdentityEcdh.ExportParameters(true));
        var initiatorIdentitySpki = initiatorIdentityEcdsa.ExportSubjectPublicKeyInfo();

        var pending = new SimulatedPeerPendingInbox();

        var bundle = CreateValidResponderPreKeyBundle();
        var responderPkh = bundle.ResponderPkh;

        var repo = new InMemoryRepository
        {
            Peers = new[]
            {
                new SimulatedPeerModel(
                    peerId: simulatedPeerId,
                    selfIdentityId: 99000,
                    displayName: "sim",
                    isOnline: true,
                    isRelayCapable: false,
                    identitySigningKeySpki: initiatorIdentitySpki,
                    identitySigningKeyPrivateKeyEcPrivateKey: initiatorIdentityPriv),
                new SimulatedPeerModel(
                    peerId: relayHostPeerId,
                    selfIdentityId: 99001,
                    displayName: "relay",
                    isOnline: true,
                    isRelayCapable: true,
                    identitySigningKeySpki: SHA256.HashData(Guid.NewGuid().ToByteArray()),
                    identitySigningKeyPrivateKeyEcPrivateKey: new byte[] { 0x01 },
                    publishedPreKeyBundles: new List<SimulatedPublishedPreKeyBundleModel>
                    {
                        new(
                            RecipientPublicKeyHash: responderPkh,
                            LogicalOwnerPeerId: new Percolator.Network.PeerId(Guid.NewGuid()),
                            IdentityKey: bundle.IdentityKey,
                            SignedPreKeyId: bundle.SignedPreKeyId,
                            SignedPreKey: bundle.SignedPreKey,
                            PreKeySignature: bundle.PreKeySignature,
                            OneTimeKeys: new ObservableList<Percolator.Cryptography.OneTimeKeyInstance>(),
                            ExpiresUtc: DateTimeOffset.UtcNow.AddMinutes(5))
                    })
            },
            Relays = new[]
            {
                new SimulatedRelayModel(relayHostPeerId)
            }
        };

        var diagnostics = new SimulatorDiagnosticsService();
        var clock = new StaticClock(StaticClock.DefaultNow);
        var sut = CreateSut(repo, diagnostics, pending, clock, out var timeProvider);
        await ((ISimulatorStateInitializer)sut).InitializeAsync(CancellationToken.None);

        // Act
        var sid = await sut.InitiateStandardHandshakeToMainByRelayPkhAsync(
            simulatedPeerId,
            relayHostPeerId,
            responderPkh,
            CancellationToken.None);

        // Assert
        sid.Should().BeNull();

        var relay = sut.Relays.Single(r => r.RelayHostPeerId == relayHostPeerId);
        var queued = relay.MessageQueue.Select(kvp => kvp.Value)
            .OfType<InboundRelayMessage>()
            .Single(i => i.DebugType == nameof(HandshakeInitiatorHello));
        queued.TargetPkh.Should().Be(IdentityPublicKeyHash.FromBytes(responderPkh));

        diagnostics.Events.Should().Contain(e =>
            e.EventType == SimulatorDiagnosticEventType.PreKeyBundleFetched
            && e.RelayHostPeerId.Value == relayHostPeerId.Value);
        diagnostics.Events.Should().Contain(e =>
            e.EventType == SimulatorDiagnosticEventType.StandardHandshakeHelloEnqueued
            && e.PeerId.Value == simulatedPeerId.Value
            && e.RelayHostPeerId.Value == relayHostPeerId.Value);
    }

    [Test]
    public async Task When_relay_returns_bundle_with_mismatched_identity_key_it_does_not_enqueue_handshake_hello()
    {
        // Arrange
        var simulatedPeerId = new Percolator.Network.PeerId(Guid.NewGuid());
        var relayHostPeerId = new Percolator.Network.PeerId(Guid.NewGuid());

        using var initiatorIdentityEcdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var initiatorIdentityPriv = initiatorIdentityEcdh.ExportECPrivateKey();
        using var initiatorIdentityEcdsa = ECDsa.Create(initiatorIdentityEcdh.ExportParameters(true));
        var initiatorIdentitySpki = initiatorIdentityEcdsa.ExportSubjectPublicKeyInfo();

        var pending = new SimulatedPeerPendingInbox();

        var requestedResponderPkh = SHA256.HashData(Guid.NewGuid().ToByteArray());

        // Bundle is valid, but its identity key hashes to a different PKH.
        var bundle = CreateValidResponderPreKeyBundle();

        var repo = new InMemoryRepository
        {
            Peers = new[]
            {
                new SimulatedPeerModel(
                    peerId: simulatedPeerId,
                    selfIdentityId: 99000,
                    displayName: "sim",
                    isOnline: true,
                    isRelayCapable: false,
                    identitySigningKeySpki: initiatorIdentitySpki,
                    identitySigningKeyPrivateKeyEcPrivateKey: initiatorIdentityPriv),
                new SimulatedPeerModel(
                    peerId: relayHostPeerId,
                    selfIdentityId: 99001,
                    displayName: "relay",
                    isOnline: true,
                    isRelayCapable: true,
                    identitySigningKeySpki: SHA256.HashData(Guid.NewGuid().ToByteArray()),
                    identitySigningKeyPrivateKeyEcPrivateKey: new byte[] { 0x01 },
                    publishedPreKeyBundles: new List<SimulatedPublishedPreKeyBundleModel>
                    {
                        new(
                            RecipientPublicKeyHash: requestedResponderPkh,
                            LogicalOwnerPeerId: new Percolator.Network.PeerId(Guid.NewGuid()),
                            IdentityKey: bundle.IdentityKey,
                            SignedPreKeyId: bundle.SignedPreKeyId,
                            SignedPreKey: bundle.SignedPreKey,
                            PreKeySignature: bundle.PreKeySignature,
                            OneTimeKeys: new ObservableList<Percolator.Cryptography.OneTimeKeyInstance>(),
                            ExpiresUtc: DateTimeOffset.UtcNow.AddMinutes(5))
                    })
            },
            Relays = new[]
            {
                new SimulatedRelayModel(relayHostPeerId)
            }
        };

        var diagnostics = new SimulatorDiagnosticsService();
        var clock = new StaticClock(StaticClock.DefaultNow);
        var sut = CreateSut(repo, diagnostics, pending, clock, out var timeProvider);
        await ((ISimulatorStateInitializer)sut).InitializeAsync(CancellationToken.None);

        // Act
        var sid = await sut.InitiateStandardHandshakeToMainByRelayPkhAsync(
            simulatedPeerId,
            relayHostPeerId,
            requestedResponderPkh,
            CancellationToken.None);

        // Assert
        sid.Should().BeNull();

        // Wait for relay queue to empty (observable behavior, not timing-dependent)
        var maxAttempts = 50; // Prevent infinite loop
        for (var i = 0; i < maxAttempts; i++)
        {
            var relay = sut.Relays.Single(r => r.RelayHostPeerId.Value == relayHostPeerId.Value);
            if (relay.MessageQueue.Count == 0)
            {
                break;
            }

            await Task.Delay(50);
        }

        var relayAfter = sut.Relays.Single(r => r.RelayHostPeerId.Value == relayHostPeerId.Value);
        relayAfter.MessageQueue.Count.Should().Be(0);

        diagnostics.Events.Should().NotContain(e => e.EventType == SimulatorDiagnosticEventType.StandardHandshakeHelloEnqueued);
    }

    [Test]
    public async Task Relayed_EstablishSessionResponse_is_handled_by_initiator_and_persists_session_with_assigned_session_id()
    {
        // Arrange
        var initiatorPeerId = new Percolator.Network.PeerId(Guid.NewGuid());
        var responderPeerId = new Percolator.Network.PeerId(Guid.NewGuid());
        var relayHostPeerId = new Percolator.Network.PeerId(Guid.NewGuid());

        using var initiatorIdentityEcdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var initiatorIdentityPriv = initiatorIdentityEcdh.ExportECPrivateKey();
        using var initiatorIdentityEcdsa = ECDsa.Create(initiatorIdentityEcdh.ExportParameters(true));
        var initiatorIdentitySpki = initiatorIdentityEcdsa.ExportSubjectPublicKeyInfo();

        using var responderIdentityEcdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var responderIdentityPriv = responderIdentityEcdh.ExportECPrivateKey();
        using var responderIdentityEcdsa = ECDsa.Create(responderIdentityEcdh.ExportParameters(true));
        var responderIdentitySpki = responderIdentityEcdsa.ExportSubjectPublicKeyInfo();

        var responderPkh = SHA256.HashData(responderIdentitySpki);

        // Create a valid responder bundle and publish it to relay under responder PKH.
        // (The bundle identity key must hash to responderPkh, so we forge it from responder keys.)
        using var responderSignedPreKey = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var responderSignedPreKeySpki = responderSignedPreKey.PublicKey.ExportSubjectPublicKeyInfo();
        var preKeySig = responderIdentityEcdsa.SignData(responderSignedPreKeySpki, HashAlgorithmName.SHA256);
        var signedPreKeyId = Guid.NewGuid();

        var repo = new InMemoryRepository
        {
            Peers = new[]
            {
                new SimulatedPeerModel(
                    peerId: initiatorPeerId,
                    selfIdentityId: 99000,
                    displayName: "init",
                    isOnline: true,
                    isRelayCapable: false,
                    identitySigningKeySpki: initiatorIdentitySpki,
                    identitySigningKeyPrivateKeyEcPrivateKey: initiatorIdentityPriv),
                new SimulatedPeerModel(
                    peerId: responderPeerId,
                    selfIdentityId: 99001,
                    displayName: "resp",
                    isOnline: true,
                    isRelayCapable: false,
                    identitySigningKeySpki: responderIdentitySpki,
                    identitySigningKeyPrivateKeyEcPrivateKey: responderIdentityPriv),
                new SimulatedPeerModel(
                    peerId: relayHostPeerId,
                    selfIdentityId: 99002,
                    displayName: "relay",
                    isOnline: true,
                    isRelayCapable: true,
                    identitySigningKeySpki: SHA256.HashData(Guid.NewGuid().ToByteArray()),
                    identitySigningKeyPrivateKeyEcPrivateKey: new byte[] { 0x01 },
                    publishedPreKeyBundles: new List<SimulatedPublishedPreKeyBundleModel>
                    {
                        new(
                            RecipientPublicKeyHash: responderPkh,
                            LogicalOwnerPeerId: responderPeerId,
                            IdentityKey: responderIdentitySpki,
                            SignedPreKeyId: signedPreKeyId,
                            SignedPreKey: responderSignedPreKeySpki,
                            PreKeySignature: preKeySig,
                            OneTimeKeys: new ObservableList<Percolator.Cryptography.OneTimeKeyInstance>(),
                            ExpiresUtc: DateTimeOffset.UtcNow.AddMinutes(5))
                    })
            },
            Relays = new[]
            {
                new SimulatedRelayModel(relayHostPeerId)
            }
        };

        // Add signed prekey to responder peer after construction
        repo.Peers.Single(p => p.PeerId == responderPeerId)
            .SignedPreKeysMutable.Add(new SimulatedSignedPreKeyModel(signedPreKeyId, responderSignedPreKey.ExportECPrivateKey(), responderSignedPreKeySpki));

        var pending = new SimulatedPeerPendingInbox();
        var diagnostics = new SimulatorDiagnosticsService();
        var clock = new StaticClock(StaticClock.DefaultNow);
        var sut = CreateSut(repo, diagnostics, pending, clock, out var timeProvider);
        await ((ISimulatorStateInitializer)sut).InitializeAsync(CancellationToken.None);

        // Act 1: initiator initiates, enqueuing HandshakeInitiatorHello to relay.
        _ = await sut.InitiateStandardHandshakeToMainByRelayPkhAsync(
            initiatorPeerId,
            relayHostPeerId,
            responderPkh,
            CancellationToken.None);

        var relay = sut.Relays.Single(r => r.RelayHostPeerId == relayHostPeerId);
        var helloQueued = relay.MessageQueue.Select(kvp => kvp.Value)
            .OfType<InboundRelayMessage>()
            .Single(i => i.DebugType == nameof(HandshakeInitiatorHello));
        var helloBytes = helloQueued.OpaqueBytes;

        // Act 2: relay delivery stores hello as pending on responder (Chunk B)
        var hello = HandshakeInitiatorHello.Parser.ParseFrom(helloBytes);
        await sut.UpsertPendingStandardSignalHelloAsync(
            recipientPeerId: responderPeerId,
            relayHostPeerId: relayHostPeerId,
            hello: hello,
            receivedUtc: StaticClock.DefaultNow,
            cancellationToken: CancellationToken.None);

        var initiatorPkhHex = Convert.ToHexString(SHA256.HashData(hello.InitiatorIdentityKeySpki.ToByteArray())).ToLowerInvariant();

        // Act 3: user accepts on responder -> EstablishSessionResponse is enqueued back to initiator via relay
        var accepted = await sut.TryAcceptPendingStandardSignalHelloAsync(
            recipientPeerId: responderPeerId,
            initiatorPkhHex: initiatorPkhHex,
            cancellationToken: CancellationToken.None);
        accepted.Should().BeTrue();

        var establishBytes = relay.MessageQueue.Select(kvp => kvp.Value)
            .OfType<InboundRelayMessage>()
            .Single(i => i.DebugType == nameof(EstablishSessionResponse))
            .OpaqueBytes;

        var establishResp = EstablishSessionResponse.Parser.ParseFrom(establishBytes);
        establishResp.Response.Should().NotBeNull();

        // Act 4: initiator consumes EstablishSessionResponse.
        _ = await sut.ReceiveRelayedOpaquePayloadAsync(initiatorPeerId, establishBytes, CancellationToken.None);

        // Assert: initiator persisted a session with the responder-assigned session id.
        var payload = EstablishSessionResponse.Types.Response.Types.ResponsePayload.Parser.ParseFrom(establishResp.Response.ResponsePayload);
        var assignedSid = new SessionId(Guid.Parse(payload.SessionId));

        // Trigger debounced persistence deterministically.
        timeProvider.Advance(TimeSpan.FromMilliseconds(300));

        sut.Peers.Single(p => p.PeerId == initiatorPeerId)
            .Sessions
            .Keys
            .Should().ContainSingle(s => s.Value == assignedSid.Value);
    }

    private sealed record ResponderBundleFixture(
        byte[] IdentityKey,
        Guid SignedPreKeyId,
        byte[] SignedPreKey,
        byte[] PreKeySignature,
        byte[] ResponderPkh);

    private static ResponderBundleFixture CreateValidResponderPreKeyBundle()
    {
        using var responderIdentityEcdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        using var responderIdentityEcdsa = ECDsa.Create(responderIdentityEcdh.ExportParameters(true));
        var responderIdentitySpki = responderIdentityEcdsa.ExportSubjectPublicKeyInfo();

        using var responderSignedPreKey = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var responderSignedPreKeySpki = responderSignedPreKey.PublicKey.ExportSubjectPublicKeyInfo();
        var preKeySig = responderIdentityEcdsa.SignData(responderSignedPreKeySpki, HashAlgorithmName.SHA256);

        var signedPreKeyId = Guid.NewGuid();

        var pkh = SHA256.HashData(responderIdentitySpki);
        return new ResponderBundleFixture(
            IdentityKey: responderIdentitySpki,
            SignedPreKeyId: signedPreKeyId,
            SignedPreKey: responderSignedPreKeySpki,
            PreKeySignature: preKeySig,
            ResponderPkh: pkh);
    }

    [Test]
    public async Task RelayHost_Returns_ResponsePayload_for_GetPreKeyBundleRequest_when_bundle_found()
    {
        // Arrange
        var relayHostPeerId = new Percolator.Network.PeerId(Guid.NewGuid());

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
            Percolator.Cryptography.Primitives.PeerId.NewId(),
            new ProtocolVersion(1),
            root,
            clock);
        var responderSession = RatchetBootstrap.CreateResponderSession(
            sessionId,
            Percolator.Cryptography.Primitives.PeerId.NewId(),
            new ProtocolVersion(1),
            root,
            clock);

        var repo = new InMemoryRepository
        {
            Peers = new[]
            {
                new SimulatedPeerModel(
                    peerId: relayHostPeerId,
                    selfIdentityId: 99000,
                    displayName: "relay",
                    isOnline: true,
                    isRelayCapable: true,
                    identitySigningKeySpki: relayIdentitySpki,
                    identitySigningKeyPrivateKeyEcPrivateKey: relayIdentityPriv,
                    publishedPreKeyBundles: new List<SimulatedPublishedPreKeyBundleModel>
                    {
                        new(
                            RecipientPublicKeyHash: bundle.ResponderPkh,
                            LogicalOwnerPeerId: new Percolator.Network.PeerId(Guid.NewGuid()),
                            IdentityKey: bundle.IdentityKey,
                            SignedPreKeyId: bundle.SignedPreKeyId,
                            SignedPreKey: bundle.SignedPreKey,
                            PreKeySignature: bundle.PreKeySignature,
                            OneTimeKeys: new ObservableList<Percolator.Cryptography.OneTimeKeyInstance>(),
                            ExpiresUtc: DateTimeOffset.UtcNow.AddMinutes(5))
                    })
            }
        };

        var diagnostics = new SimulatorDiagnosticsService();
        var sut = CreateSut(repo, diagnostics, pending, clock, out var timeProvider);
        await ((ISimulatorStateInitializer)sut).InitializeAsync(CancellationToken.None);

        sut.Peers.Single(p => p.PeerId == relayHostPeerId).SessionsMutable[responderSession.Id] = responderSession;

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
        var relayHostPeerId = new Percolator.Network.PeerId(Guid.NewGuid());

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
            Percolator.Cryptography.Primitives.PeerId.NewId(),
            new ProtocolVersion(1),
            root,
            clock);
        var responderSession = RatchetBootstrap.CreateResponderSession(
            sessionId,
            Percolator.Cryptography.Primitives.PeerId.NewId(),
            new ProtocolVersion(1),
            root,
            clock);

        var repo = new InMemoryRepository
        {
            Peers = new[]
            {
                new SimulatedPeerModel(
                    peerId: relayHostPeerId,
                    selfIdentityId: 99000,
                    displayName: "relay",
                    isOnline: true,
                    isRelayCapable: true,
                    identitySigningKeySpki: SHA256.HashData(Guid.NewGuid().ToByteArray()),
                    identitySigningKeyPrivateKeyEcPrivateKey: new byte[] { 0x01 })
            }
        };

        var diagnostics = new SimulatorDiagnosticsService();
        var sut = CreateSut(repo, diagnostics, pending, clock, out var timeProvider);
        await ((ISimulatorStateInitializer)sut).InitializeAsync(CancellationToken.None);

        sut.Peers.Single(p => p.PeerId == relayHostPeerId).SessionsMutable[responderSession.Id] = responderSession;

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
        deliverResp.ResultCase.Should().Be(DeliverOpaqueMessageResponse.ResultOneofCase.Never);
        deliverResp.ResponsePayload.Should().BeNull();
    }
}

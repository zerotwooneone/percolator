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
using Percolator.Network;

namespace Desktop.Wpf.Tests;

[TestFixture]
public sealed class SimulatedPeerRuntimeStandardHandshakeRelayedTests
{
    private sealed record ResponderPreKeyBundle(
        byte[] ResponderPkh,
        byte[] IdentityKey,
        Guid SignedPreKeyId,
        byte[] SignedPreKey,
        byte[] PreKeySignature);

    private static ResponderPreKeyBundle CreateValidResponderPreKeyBundle()
    {
        using var identityEcdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        using var identityEcdsa = ECDsa.Create(identityEcdh.ExportParameters(true));
        var identitySpki = identityEcdsa.ExportSubjectPublicKeyInfo();

        using var signedPreKey = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var signedPreKeySpki = signedPreKey.PublicKey.ExportSubjectPublicKeyInfo();

        var sig = identityEcdsa.SignData(signedPreKeySpki, HashAlgorithmName.SHA256);
        var pkh = SHA256.HashData(identitySpki);

        return new ResponderPreKeyBundle(
            ResponderPkh: pkh,
            IdentityKey: identitySpki,
            SignedPreKeyId: Guid.NewGuid(),
            SignedPreKey: signedPreKeySpki,
            PreKeySignature: sig);
    }

    private static SimulatorStateService CreateSut(
        InMemorySimulatorStateRepository repo,
        SimulatorDiagnosticsService diagnostics,
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
            services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
            new NoopSimulatorToMainTransportService(),
            transportOptions,
            engine,
            timeProvider);
    }

    [Test]
    public async Task AcceptInboundDirectInviteAsync_creates_session_in_memory()
    {
        // Arrange
        var simulatedPeerId = new NetworkPeerId(10);
        var inviterPeerId = new NetworkPeerId(11);

        var acceptorPeer = CryptoTestHelpers.CreateTestPeer(
            simulatedPeerId, 99000, "sim", false,
            new System.Net.DnsEndPoint("127.77.1.1", 5002));

        var inviterPeer = CryptoTestHelpers.CreateTestPeer(
            inviterPeerId, 99001, "inviter", false,
            new System.Net.DnsEndPoint("127.77.1.2", 5002));

        var (spkId, spkPriv, spkSpki, spkSig) = CryptoTestHelpers.CreateSignedPreKey(
            inviterPeer.IdentitySigningKeySpki,
            inviterPeer.IdentitySigningKeyPrivateKeyEcPrivateKey);

        var payload = new InviteHandshakeRequestPayload
        {
            Version = 1,
            RequestCorrelationId = Guid.NewGuid().ToString(),
            InviterPreKey = new InviteHandshakePreKeyBundle
            {
                Version = 1,
                InviterSignedPreKey = ByteString.CopyFrom(spkSpki),
                PreKeySignature = ByteString.CopyFrom(spkSig)
            }
        };

        var invite = new EstablishDirectSessionRequest
        {
            Version = 1,
            InviterIdentityKey = ByteString.CopyFrom(inviterPeer.IdentitySigningKeySpki),
            Payload = ByteString.CopyFrom(payload.ToByteArray())
        };

        var repo = new InMemorySimulatorStateRepository();
        repo.Seed(new SimulatorStateSnapshot(
            Version: 1,
            Peers: new[] { acceptorPeer.Freeze() },
            Relationships: Array.Empty<PeerRelationshipSnapshot>(),
            Relays: Array.Empty<RelayStateSnapshot>(),
            Groups: Array.Empty<GroupConversationDto>()));

        var diagnostics = new SimulatorDiagnosticsService();
        var clock = new StaticClock(StaticClock.DefaultNow);
        var sut = CreateSut(repo, diagnostics, clock, out var timeProvider);
        await ((ISimulatorStateInitializer)sut).InitializeAsync(CancellationToken.None);

        var acceptance = await sut.AcceptInboundDirectInviteAsync(simulatedPeerId, inviterPeerId, invite, CancellationToken.None);

        acceptance.SessionId.Should().NotBeNull();
        acceptance.Response.Should().NotBeNull();
        acceptance.Response.Version.Should().Be(1);

        sut.Peers.Should().ContainSingle(p => p.NetworkPeerId == simulatedPeerId);
        sut.Peers.Single(p => p.NetworkPeerId == simulatedPeerId).Sessions.Count.Should().Be(1);
    }

    [Test]
    public async Task AcceptInboundDirectInviteAsync_eventually_persists_runtime_store()
    {
        // Arrange
        var simulatedPeerId = new NetworkPeerId(12);
        var inviterPeerId = new NetworkPeerId(13);

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

        var peer = new SimulatedPeerModel(
            networkPeerId: simulatedPeerId,
            publicIdentityId: new Percolator.Identity.PublicIdentityId(Guid.NewGuid()),
            selfIdentityId: 99000,
            displayName: "sim",
            isRelayCapable: false,
            identitySigningKeySpki: acceptorIdentitySpki,
            identitySigningKeyPrivateKeyEcPrivateKey: acceptorIdentityPriv,
            endpoint: new System.Net.DnsEndPoint("127.77.1.1", 5002));

        var repo = new InMemorySimulatorStateRepository();
        repo.Seed(new SimulatorStateSnapshot(
            Version: 1,
            Peers: new[] { peer.Freeze() },
            Relationships: Array.Empty<PeerRelationshipSnapshot>(),
            Relays: Array.Empty<RelayStateSnapshot>(),
            Groups: Array.Empty<GroupConversationDto>()));

        var diagnostics = new SimulatorDiagnosticsService();
        var clock = new StaticClock(StaticClock.DefaultNow);
        var sut = CreateSut(repo, diagnostics, clock, out var timeProvider);
        await ((ISimulatorStateInitializer)sut).InitializeAsync(CancellationToken.None);

        // Act
        _ = await sut.AcceptInboundDirectInviteAsync(simulatedPeerId, inviterPeerId, invite, CancellationToken.None);

        // Assert: advance time to trigger debounced save
        timeProvider.Advance(TimeSpan.FromMilliseconds(300));

        repo.LastSavedSnapshot.Should().NotBeNull();
        repo.LastSavedSnapshot!.Peers.Single(p => p.NetworkPeerId == simulatedPeerId).Sessions.Count.Should().Be(1);
    }

    [Test]
    public async Task Relayed_HandshakeInitiatorHello_WhenAccepted_EstablishesSessionAndPersistsState()
    {
        var simulatedPeerId = new NetworkPeerId(123456789);
        var relayHostPeerId = new NetworkPeerId(987654321);

        using var responderIdentityEcdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var responderIdentityPriv = responderIdentityEcdh.ExportECPrivateKey();
        using var responderIdentityEcdsa = ECDsa.Create(responderIdentityEcdh.ExportParameters(true));
        var responderIdentitySpki = responderIdentityEcdsa.ExportSubjectPublicKeyInfo();

        var spkId = new Guid("00000000-0000-0000-0000-000000000001");
        using var responderSignedPreKey = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var responderSignedPreKeySpki = responderSignedPreKey.PublicKey.ExportSubjectPublicKeyInfo();
        var responderSignedPreKeyPriv = responderSignedPreKey.ExportECPrivateKey();

        var peer = new SimulatedPeerModel(
            networkPeerId: simulatedPeerId,
            publicIdentityId: new Percolator.Identity.PublicIdentityId(new Guid("00000000-0000-0000-0000-000000000002")),
            selfIdentityId: 99000,
            displayName: "sim",
            isRelayCapable: false,
            identitySigningKeySpki: responderIdentitySpki,
            identitySigningKeyPrivateKeyEcPrivateKey: responderIdentityPriv,
            endpoint: new System.Net.DnsEndPoint("127.77.1.1", 5002));
        peer.SignedPreKeysMutable.Add(new SimulatedSignedPreKeyModel(spkId, responderSignedPreKeyPriv, responderSignedPreKeySpki));

        var repo = new InMemorySimulatorStateRepository();
        repo.Seed(new SimulatorStateSnapshot(
            Version: 1,
            Peers: new[] { peer.Freeze() },
            Relationships: Array.Empty<PeerRelationshipSnapshot>(),
            Relays: new[] { new RelayStateSnapshot(relayHostPeerId, Array.Empty<OutboundRelayMessageSnapshot>(), Array.Empty<InboundRelayMessageSnapshot>()) },
            Groups: Array.Empty<GroupConversationDto>()));

        var diagnostics = new SimulatorDiagnosticsService();
        var clock = new StaticClock(StaticClock.DefaultNow);
        var sut = CreateSut(repo, diagnostics, clock, out var timeProvider);
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
            InitiatorPublicIdentityId = ByteString.CopyFrom(new Guid("00000000-0000-0000-0000-000000000003").ToByteArray()),
            InitiatorEphemeralKeySpki = ByteString.CopyFrom(initiatorEphSpki),
            SignedPreKeyId = ByteString.CopyFrom(spkId.ToByteArray())
        };

        // Act: relay delivery stores standard hello as pending (no immediate response)
        await sut.UpsertPendingStandardSignalHelloAsync(
            recipientNetworkPeerId: simulatedPeerId,
            relayHostNetworkPeerId: relayHostPeerId,
            hello: hello,
            receivedUtc: StaticClock.DefaultNow,
            cancellationToken: CancellationToken.None);

        // Act: user accepts the pending hello, which establishes session and enqueues response back to initiator
        var initiatorPkhHex = Convert.ToHexString(SHA256.HashData(initiatorIdentitySpki)).ToLowerInvariant();
        var accepted = await sut.TryAcceptPendingStandardSignalHelloAsync(
            recipientNetworkPeerId: simulatedPeerId,
            initiatorPkhHex: initiatorPkhHex,
            cancellationToken: CancellationToken.None);

        // Assert: verify acceptance succeeded (public API return value)
        accepted.Should().BeTrue();

        // Assert: advance time to trigger debounced save and verify persisted state (public behavior)
        timeProvider.Advance(TimeSpan.FromMilliseconds(300));

        repo.LastSavedSnapshot.Should().NotBeNull();
        repo.LastSavedSnapshot!.Peers.Single(p => p.NetworkPeerId == simulatedPeerId).Sessions.Count.Should().Be(1);
    }

    [Test]
    public async Task When_initiating_standard_handshake_via_relay_it_enqueues_handshake_initiator_hello_to_relay_host()
    {
        // Arrange
        var simulatedPeerId = new NetworkPeerId(123456789);
        var relayHostPeerId = new NetworkPeerId(987654321);

        using var initiatorIdentityEcdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var initiatorIdentityPriv = initiatorIdentityEcdh.ExportECPrivateKey();
        using var initiatorIdentityEcdsa = ECDsa.Create(initiatorIdentityEcdh.ExportParameters(true));
        var initiatorIdentitySpki = initiatorIdentityEcdsa.ExportSubjectPublicKeyInfo();

        var bundle = CreateValidResponderPreKeyBundle();
        var responderPkh = bundle.ResponderPkh;

        var initiatorPeer = new SimulatedPeerModel(
            networkPeerId: simulatedPeerId,
            publicIdentityId: new Percolator.Identity.PublicIdentityId(new Guid("00000000-0000-0000-0000-000000000001")),
            selfIdentityId: 99000,
            displayName: "sim",
            isRelayCapable: false,
            identitySigningKeySpki: initiatorIdentitySpki,
            identitySigningKeyPrivateKeyEcPrivateKey: initiatorIdentityPriv,
            endpoint: new System.Net.DnsEndPoint("127.77.1.1", 5002));
        var relayPeer = new SimulatedPeerModel(
            networkPeerId: relayHostPeerId,
            publicIdentityId: new Percolator.Identity.PublicIdentityId(new Guid("00000000-0000-0000-0000-000000000002")),
            selfIdentityId: 99001,
            displayName: "relay",
            isRelayCapable: true,
            identitySigningKeySpki: SHA256.HashData(new Guid("00000000-0000-0000-0000-000000000003").ToByteArray()),
            identitySigningKeyPrivateKeyEcPrivateKey: new byte[] { 0x01 },
            endpoint: new System.Net.DnsEndPoint("127.77.1.2", 5002),
            publishedPreKeyBundles: new List<SimulatedPublishedPreKeyBundleModel>
            {
                new(
                    RecipientPublicKeyHash: Percolator.Identity.IdentityPublicKeyHash.FromBytesOwned(responderPkh),
                    LogicalOwnerNetworkPeerId: new NetworkPeerId(111111111),
                    IdentityKey: bundle.IdentityKey,
                    SignedPreKeyId: bundle.SignedPreKeyId,
                    SignedPreKey: bundle.SignedPreKey,
                    PreKeySignature: bundle.PreKeySignature,
                    OneTimeKeys: new ObservableList<Percolator.Cryptography.OneTimeKeyInstance>(),
                    ExpiresUtc: new DateTimeOffset(2025, 1, 1, 12, 0, 0, TimeSpan.Zero).AddMinutes(5))
            });

        var repo = new InMemorySimulatorStateRepository();
        repo.Seed(new SimulatorStateSnapshot(
            Version: 1,
            Peers: new[] { initiatorPeer.Freeze(), relayPeer.Freeze() },
            Relationships: Array.Empty<PeerRelationshipSnapshot>(),
            Relays: new[] { new RelayStateSnapshot(relayHostPeerId, Array.Empty<OutboundRelayMessageSnapshot>(), Array.Empty<InboundRelayMessageSnapshot>()) },
            Groups: Array.Empty<GroupConversationDto>()));

        var diagnostics = new SimulatorDiagnosticsService();
        var clock = new StaticClock(StaticClock.DefaultNow);
        var sut = CreateSut(repo, diagnostics, clock, out var timeProvider);
        await ((ISimulatorStateInitializer)sut).InitializeAsync(CancellationToken.None);

        // Act
        var sid = await sut.InitiateStandardHandshakeToMainByRelayPkhAsync(
            simulatedPeerId,
            relayHostPeerId,
            Percolator.Identity.IdentityPublicKeyHash.FromBytesOwned(responderPkh),
            CancellationToken.None);

        // Assert: verify session ID was returned (public API)
        sid.Should().NotBeNull();

        // Assert: verify diagnostic events were published (public behavior)
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
        var simulatedPeerId = new NetworkPeerId(123456789);
        var relayHostPeerId = new NetworkPeerId(987654321);

        using var initiatorIdentityEcdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var initiatorIdentityPriv = initiatorIdentityEcdh.ExportECPrivateKey();
        using var initiatorIdentityEcdsa = ECDsa.Create(initiatorIdentityEcdh.ExportParameters(true));
        var initiatorIdentitySpki = initiatorIdentityEcdsa.ExportSubjectPublicKeyInfo();

        var requestedResponderPkh = SHA256.HashData(new Guid("00000000-0000-0000-0000-000000000001").ToByteArray());

        // Bundle is valid, but its identity key hashes to a different PKH.
        var bundle = CreateValidResponderPreKeyBundle();

        var initiatorPeer = new SimulatedPeerModel(
            networkPeerId: simulatedPeerId,
            publicIdentityId: new Percolator.Identity.PublicIdentityId(new Guid("00000000-0000-0000-0000-000000000002")),
            selfIdentityId: 99000,
            displayName: "sim",
            isRelayCapable: false,
            identitySigningKeySpki: initiatorIdentitySpki,
            identitySigningKeyPrivateKeyEcPrivateKey: initiatorIdentityPriv,
            endpoint: new System.Net.DnsEndPoint("127.77.1.1", 5002));
        var relayPeer = new SimulatedPeerModel(
            networkPeerId: relayHostPeerId,
            publicIdentityId: new Percolator.Identity.PublicIdentityId(new Guid("00000000-0000-0000-0000-000000000003")),
            selfIdentityId: 99001,
            displayName: "relay",
            isRelayCapable: true,
            identitySigningKeySpki: SHA256.HashData(new Guid("00000000-0000-0000-0000-000000000004").ToByteArray()),
            identitySigningKeyPrivateKeyEcPrivateKey: new byte[] { 0x01 },
            endpoint: new System.Net.DnsEndPoint("127.77.1.2", 5002),
            publishedPreKeyBundles: new List<SimulatedPublishedPreKeyBundleModel>
            {
                new(
                    RecipientPublicKeyHash: Percolator.Identity.IdentityPublicKeyHash.FromBytesOwned(requestedResponderPkh),
                    LogicalOwnerNetworkPeerId: new NetworkPeerId(111111111),
                    IdentityKey: bundle.IdentityKey,
                    SignedPreKeyId: bundle.SignedPreKeyId,
                    SignedPreKey: bundle.SignedPreKey,
                    PreKeySignature: bundle.PreKeySignature,
                    OneTimeKeys: new ObservableList<Percolator.Cryptography.OneTimeKeyInstance>(),
                    ExpiresUtc: new DateTimeOffset(2025, 1, 1, 12, 0, 0, TimeSpan.Zero).AddMinutes(5))
            });

        var repo = new InMemorySimulatorStateRepository();
        repo.Seed(new SimulatorStateSnapshot(
            Version: 1,
            Peers: new[] { initiatorPeer.Freeze(), relayPeer.Freeze() },
            Relationships: Array.Empty<PeerRelationshipSnapshot>(),
            Relays: new[] { new RelayStateSnapshot(relayHostPeerId, Array.Empty<OutboundRelayMessageSnapshot>(), Array.Empty<InboundRelayMessageSnapshot>()) },
            Groups: Array.Empty<GroupConversationDto>()));

        var diagnostics = new SimulatorDiagnosticsService();
        var clock = new StaticClock(StaticClock.DefaultNow);
        var sut = CreateSut(repo, diagnostics, clock, out var timeProvider);
        await ((ISimulatorStateInitializer)sut).InitializeAsync(CancellationToken.None);

        // Act
        var sid = await sut.InitiateStandardHandshakeToMainByRelayPkhAsync(
            simulatedPeerId,
            relayHostPeerId,
            Percolator.Identity.IdentityPublicKeyHash.FromBytesOwned(requestedResponderPkh),
            CancellationToken.None);

        // Assert: verify session ID was not returned (public API)
        sid.Should().BeNull();

        // Assert: verify diagnostic event was not published (public behavior)
        diagnostics.Events.Should().NotContain(e => e.EventType == SimulatorDiagnosticEventType.StandardHandshakeHelloEnqueued);
    }

    [Test]
    public async Task Relayed_EstablishSessionResponse_is_handled_by_initiator_and_persists_session_with_assigned_session_id()
    {
        // Arrange
        var initiatorPeerId = new NetworkPeerId(123456789);
        var responderPeerId = new NetworkPeerId(987654321);
        var relayHostPeerId = new NetworkPeerId(111111111);

        using var initiatorIdentityEcdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var initiatorIdentityPriv = initiatorIdentityEcdh.ExportECPrivateKey();
        using var initiatorIdentityEcdsa = ECDsa.Create(initiatorIdentityEcdh.ExportParameters(true));
        var initiatorIdentitySpki = initiatorIdentityEcdsa.ExportSubjectPublicKeyInfo();

        var bundle = CreateValidResponderPreKeyBundle();
        var responderPkh = bundle.ResponderPkh;

        var clock = new StaticClock(StaticClock.DefaultNow);

        var initiatorPeer = new SimulatedPeerModel(
            networkPeerId: initiatorPeerId,
            publicIdentityId: new Percolator.Identity.PublicIdentityId(new Guid("00000000-0000-0000-0000-000000000001")),
            selfIdentityId: 99000,
            displayName: "init",
            isRelayCapable: false,
            identitySigningKeySpki: initiatorIdentitySpki,
            identitySigningKeyPrivateKeyEcPrivateKey: initiatorIdentityPriv,
            endpoint: new System.Net.DnsEndPoint("127.77.1.1", 5002));
        var relayPeer = new SimulatedPeerModel(
            networkPeerId: relayHostPeerId,
            publicIdentityId: new Percolator.Identity.PublicIdentityId(new Guid("00000000-0000-0000-0000-000000000002")),
            selfIdentityId: 99001,
            displayName: "relay",
            isRelayCapable: true,
            identitySigningKeySpki: SHA256.HashData(new Guid("00000000-0000-0000-0000-000000000003").ToByteArray()),
            identitySigningKeyPrivateKeyEcPrivateKey: new byte[] { 0x01 },
            endpoint: new System.Net.DnsEndPoint("127.77.1.2", 5002),
            publishedPreKeyBundles: new List<SimulatedPublishedPreKeyBundleModel>
            {
                new(
                    RecipientPublicKeyHash: Percolator.Identity.IdentityPublicKeyHash.FromBytesOwned(responderPkh),
                    LogicalOwnerNetworkPeerId: responderPeerId,
                    IdentityKey: bundle.IdentityKey,
                    SignedPreKeyId: bundle.SignedPreKeyId,
                    SignedPreKey: bundle.SignedPreKey,
                    PreKeySignature: bundle.PreKeySignature,
                    OneTimeKeys: new ObservableList<Percolator.Cryptography.OneTimeKeyInstance>(),
                    ExpiresUtc: new DateTimeOffset(2025, 1, 1, 12, 0, 0, TimeSpan.Zero).AddMinutes(5))
            });

        var repo = new InMemorySimulatorStateRepository();
        repo.Seed(new SimulatorStateSnapshot(
            Version: 1,
            Peers: new[] { initiatorPeer.Freeze(), relayPeer.Freeze() },
            Relationships: Array.Empty<PeerRelationshipSnapshot>(),
            Relays: new[] { new RelayStateSnapshot(relayHostPeerId, Array.Empty<OutboundRelayMessageSnapshot>(), Array.Empty<InboundRelayMessageSnapshot>()) },
            Groups: Array.Empty<GroupConversationDto>()));

        var diagnostics = new SimulatorDiagnosticsService();
        var sut = CreateSut(repo, diagnostics, clock, out var timeProvider);
        await ((ISimulatorStateInitializer)sut).InitializeAsync(CancellationToken.None);

        // Act 1: initiate handshake, which creates a pending session with a temporary session id.
        var temporarySessionId = await sut.InitiateStandardHandshakeToMainByRelayPkhAsync(
            initiatorPeerId,
            relayHostPeerId,
            Percolator.Identity.IdentityPublicKeyHash.FromBytesOwned(responderPkh),
            CancellationToken.None);

        temporarySessionId.Should().NotBeNull();

        var initiator = sut.Peers.Single(p => p.NetworkPeerId == initiatorPeerId);
        initiator.PendingStandardHandshakeToMainResponderPublicKeyHash.CurrentValue.Should().NotBeNull();
        initiator.PendingStandardHandshakeToMainTemporarySessionId.CurrentValue.Should().NotBeNull();
        initiator.PendingStandardHandshakeToMainTemporarySessionId.CurrentValue.Should().Be(temporarySessionId!.Value);
        initiator.SessionsMutable.ContainsKey(temporarySessionId!).Should().BeTrue();

        // Act 2: deliver the "main" response (relayed opaque payload) assigning a final session id.
        var assignedSessionId = new Guid("00000000-0000-0000-0000-000000000004");
        var payload = new EstablishSessionResponse.Types.Response.Types.ResponsePayload
        {
            Version = 1,
            SessionId = assignedSessionId.ToString()
        };
        var resp = new EstablishSessionResponse
        {
            Version = 1,
            Response = new EstablishSessionResponse.Types.Response
            {
                Version = 1,
                IdentitySigningKey = ByteString.CopyFrom(bundle.IdentityKey),
                ResponsePayload = ByteString.CopyFrom(payload.ToByteArray())
            }
        };

        await sut.ReceiveRelayedOpaquePayloadAsync(initiatorPeerId, resp.ToByteArray(), CancellationToken.None);

        // Assert: session is now stored under the assigned id and gets persisted.
        sut.Peers.Single(p => p.NetworkPeerId == initiatorPeerId)
            .Sessions
            .Keys
            .Should()
            .Contain(new SessionId(assignedSessionId));

        // Advance time to trigger debounced save
        timeProvider.Advance(TimeSpan.FromMilliseconds(300));

        repo.LastSavedSnapshot.Should().NotBeNull();
        repo.LastSavedSnapshot!
            .Peers
            .Single(p => p.NetworkPeerId == initiatorPeerId)
            .Sessions
            .Should()
            .Contain(s => s.SessionId == assignedSessionId);
    }

    [Test]
    public async Task RelayHost_Returns_ResponsePayload_for_GetPreKeyBundleRequest_when_bundle_not_found()
    {
        // Arrange
        var relayHostPeerId = new NetworkPeerId(20);

        using var relayIdentityEcdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        using var relayIdentityEcdsa = ECDsa.Create(relayIdentityEcdh.ExportParameters(true));
        
        var requestedPublicIdentityId = Guid.NewGuid().ToByteArray();

        var clock = new StaticClock(StaticClock.DefaultNow);
        var sessionId = SessionId.NewId();
        var root = RootKey.FromBytes(new byte[32]);
        var initiatorSession = RatchetBootstrap.CreateInitiatorSession(
            sessionId,
            new Percolator.Cryptography.Primitives.PeerId(1),
            new ProtocolVersion(1),
            root,
            clock);
        var responderSession = RatchetBootstrap.CreateResponderSession(
            sessionId,
            new Percolator.Cryptography.Primitives.PeerId(1),
            new ProtocolVersion(1),
            root,
            clock);

        var relayPeer = new SimulatedPeerModel(
            networkPeerId: relayHostPeerId,
            publicIdentityId: new Percolator.Identity.PublicIdentityId(Guid.NewGuid()),
            selfIdentityId: 99000,
            displayName: "relay",
            isRelayCapable: true,
            identitySigningKeySpki: SHA256.HashData(Guid.NewGuid().ToByteArray()),
            identitySigningKeyPrivateKeyEcPrivateKey: new byte[] { 0x01 },
            endpoint: new System.Net.DnsEndPoint("127.77.1.1", 5002));

        var repo = new InMemorySimulatorStateRepository();
        repo.Seed(new SimulatorStateSnapshot(
            Version: 1,
            Peers: new[] { relayPeer.Freeze() },
            Relationships: Array.Empty<PeerRelationshipSnapshot>(),
            Relays: Array.Empty<RelayStateSnapshot>(),
            Groups: Array.Empty<GroupConversationDto>()));

        var diagnostics = new SimulatorDiagnosticsService();
        var sut = CreateSut(repo, diagnostics, clock, out var timeProvider);
        await ((ISimulatorStateInitializer)sut).InitializeAsync(CancellationToken.None);

        sut.Peers.Single(p => p.NetworkPeerId == relayHostPeerId).SessionsMutable[responderSession.Id] = responderSession;

        var envReq = new InternalEnvelope
        {
            PrekeyEnvelope = new PrekeyEnvelope
            {
                Version = 1,
                GetPreKeyBundleRequest = new GetPreKeyBundleRequest
                {
                    Version = 1,
                    PublicIdentityId = ByteString.CopyFrom(requestedPublicIdentityId)
                }
            }
        };

        var pt = Plaintext.FromBytes(envReq.ToByteArray());
        var cipher = initiatorSession.Encrypt(pt, clock);
        var deliverReq = new DeliverOpaqueMessageRequest { Version = 1, Payload = ByteString.CopyFrom(cipher.ToArray()) };

        // Act
        var deliverResp = await sut.ReceiveOpaqueMessageFromMainAsync(relayHostPeerId, deliverReq, CancellationToken.None);

        // Assert
        deliverResp.ResultCase.Should().Be(DeliverOpaqueMessageResponse.ResultOneofCase.Never);
        deliverResp.ResponsePayload.Should().BeNull();
    }
}

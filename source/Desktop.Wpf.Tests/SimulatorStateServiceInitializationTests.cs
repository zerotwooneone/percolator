using System;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Desktop.Wpf.Features.Simulator;
using Desktop.Wpf.Features.Simulator.Protocol;
using Desktop.Wpf.Features.Sessions;
using Desktop.Wpf.Features.Simulator.Models;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NUnit.Framework;
using Percolator.Application.Configuration;
using Percolator.Cryptography;

namespace Desktop.Wpf.Tests;

[TestFixture]
public sealed class SimulatorStateServiceInitializationTests
{
    [Test]
    public async Task InitializeAsync_CoalescesConcurrentCalls()
    {
        // Arrange
        var innerStore = new InMemorySimulatorStateRepository();
        var store = new GatedLoadSimulatorStateRepository(innerStore);
        var diagnostics = new SimulatorDiagnosticsService();
        var fakeTimeProvider = new FakeTimeProvider();

        var services = new ServiceCollection();
        services.AddSingleton<IClock, SystemClock>();
        var sp = services.BuildServiceProvider();
        var scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();

        var transportOptions = Options.Create(new TransportOptions { SimulatorPort = 5002 });
        var engine = new SignalProtocolEngine(new SystemClock());

        var sut = new SimulatorStateService(store, diagnostics, scopeFactory, new NoopSimulatorToMainTransportService(), transportOptions, engine, fakeTimeProvider);

        // Act: Call InitializeAsync concurrently
        var initializer = (ISimulatorStateInitializer)sut;
        var t1 = initializer.InitializeAsync();
        var t2 = initializer.InitializeAsync();

        // Wait for the load to be called (checking LoadCallCount to verify coalescing behavior)
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (Volatile.Read(ref innerStore.LoadCallCount) == 0 && !cts.IsCancellationRequested)
        {
            await Task.Delay(10, cts.Token);
        }

        Volatile.Read(ref innerStore.LoadCallCount).Should().Be(1);

        store.ReleaseLoadWith(new SimulatorStateSnapshot(
            Version: 1,
            Peers: Array.Empty<PeerStateSnapshot>(),
            Relationships: Array.Empty<PeerRelationshipSnapshot>(),
            Relays: Array.Empty<RelayStateSnapshot>(),
            Groups: Array.Empty<GroupConversationDto>()));
        await Task.WhenAll(t1, t2);

        // Assert: Only one load call occurred (coalescing worked)
        innerStore.LoadCallCount.Should().Be(1);
    }

    [Test]
    public async Task InitializeAsync_RestoresSessionsFromSnapshot()
    {
        // Arrange
        var peerId = new Percolator.Network.NetworkPeerId(Guid.NewGuid());
        var remotePeerId = new Percolator.Cryptography.Primitives.PeerId(Guid.NewGuid());
        var signedPreKeyId = Guid.NewGuid();

        using var identity = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var priv = identity.ExportECPrivateKey();
        var spki = identity.ExportSubjectPublicKeyInfo();

        var model = new SimulatedPeerModel(
            networkPeerId: peerId,
            selfIdentityId: 99000,
            displayName: "Alice",
            isRelayCapable: false,
            identitySigningKeySpki: spki,
            identitySigningKeyPrivateKeyEcPrivateKey: priv,
            endpoint: new System.Net.DnsEndPoint("127.77.1.1", 5002));

        // Setup: Add test data to model (using mutable collections since snapshot creation requires frozen state)
        model.SignedPreKeysMutable.Add(new SimulatedSignedPreKeyModel(
            SignedPreKeyId: signedPreKeyId,
            PrivateEcPrivateKey: new byte[] { 1, 2, 3 },
            PublicSpki: new byte[] { 4, 5, 6 }));

        // Add session
        var ratchet = new RatchetState(
            rootKey: RootKey.FromBytes(new byte[32]),
            sendingChainKey: null,
            sendingCounter: 7,
            receivingChainKey: null,
            receivingCounter: 8,
            previousChainLength: 0,
            remoteRatchetKey: null,
            dhRatchetPrivateKey: null,
            skippedKeyLimit: 1000);

        var session = SecureSession.Create(
            id: new SessionId(Guid.NewGuid()),
            remotePeerId: remotePeerId,
            protocolVersion: new ProtocolVersion(1),
            state: ratchet,
            sessionCrypto: new AeadSessionCrypto(),
            clock: new TestClock(DateTimeOffset.UtcNow));

        model.SessionsMutable[session.Id] = session;

        // Add outbound invite
        var inviteCorrelationId = Guid.NewGuid();
        model.OutboundInvitesMutable.Add(new SimulatedOutboundInviteModel(
            inviteCorrelationId,
            new byte[] { 10, 11, 12 }));
        
        var snapshot = new SimulatorStateSnapshot(
            Version: 1,
            Peers: new[] { model.Freeze() },
            Relationships: Array.Empty<PeerRelationshipSnapshot>(),
            Relays: Array.Empty<RelayStateSnapshot>(),
            Groups: Array.Empty<GroupConversationDto>());

        var innerStore = new InMemorySimulatorStateRepository();
        var store = new GatedLoadSimulatorStateRepository(innerStore);
        store.ReleaseLoadWith(snapshot);

        var diagnostics = new SimulatorDiagnosticsService();
        var fakeTimeProvider = new FakeTimeProvider();
        var services = new ServiceCollection();
        services.AddSingleton<IClock, SystemClock>();
        var sp = services.BuildServiceProvider();
        var scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();
        var transportOptions = Options.Create(new TransportOptions { SimulatorPort = 5002 });
        var engine = new SignalProtocolEngine(new SystemClock());

        var sut = new SimulatorStateService(
            store,
            diagnostics,
            scopeFactory,
            new NoopSimulatorToMainTransportService(),
            transportOptions,
            engine,
            fakeTimeProvider);

        // Act
        var initializer = (ISimulatorStateInitializer)sut;
        await initializer.InitializeAsync();

        // Assert: Verify state was restored correctly (checking internal collections to verify restoration)
        var restoredPeer = sut.Peers.FirstOrDefault(p => p.NetworkPeerId == peerId);
        restoredPeer.Should().NotBeNull();

        restoredPeer.SignedPreKeysMutable.Should().HaveCount(1);
        restoredPeer.SignedPreKeysMutable.First().SignedPreKeyId.Should().Be(signedPreKeyId);

        restoredPeer.SessionsMutable.Should().HaveCount(1);
        restoredPeer.SessionsMutable.ContainsKey(session.Id).Should().BeTrue();

        restoredPeer.OutboundInvitesMutable.Should().HaveCount(1);
        restoredPeer.OutboundInvitesMutable.First().CorrelationId.Should().Be(inviteCorrelationId);
    }

    [Test]
    public async Task PublishPreKeyBundle_WithMultipleOnetimeKeys_PopsOneAtATime()
    {
        // Arrange
        var expiresUtc = DateTimeOffset.UtcNow.AddDays(1);

        var services = new ServiceCollection();
        services.AddSingleton<IClock, SystemClock>();
        var sp = services.BuildServiceProvider();
        var scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();

        var diagnostics = new SimulatorDiagnosticsService();
        var fakeTimeProvider = new FakeTimeProvider();
        var transportOptions = Options.Create(new TransportOptions { SimulatorPort = 5002 });
        var engine = new SignalProtocolEngine(new SystemClock());

        var innerStore = new InMemorySimulatorStateRepository();
        var store = new GatedLoadSimulatorStateRepository(innerStore);

        var sut = new SimulatorStateService(
            store,
            diagnostics,
            scopeFactory,
            new NoopSimulatorToMainTransportService(),
            transportOptions,
            engine,
            fakeTimeProvider);

        // Release empty snapshot so InitializeAsync can complete
        store.ReleaseLoadWith(new SimulatorStateSnapshot(
            Version: 1,
            Peers: Array.Empty<PeerStateSnapshot>(),
            Relationships: Array.Empty<PeerRelationshipSnapshot>(),
            Relays: Array.Empty<RelayStateSnapshot>(),
            Groups: Array.Empty<GroupConversationDto>()));

        await sut.InitializeAsync(CancellationToken.None);

        // Add the relay host peer
        var relayHostPeerId = await sut.AddPeerAsync(
            displayName: "relay",
            cancellationToken: CancellationToken.None);

        // Add the simulated peer
        var simulatedPeerId = await sut.AddPeerAsync(
            displayName: "sim",
            cancellationToken: CancellationToken.None);

        // Act: Publish a bundle with 3 onetime keys
        await sut.PublishStandardPreKeyBundleToRelayAsync(
            simulatedPeerId,
            relayHostPeerId,
            expiresUtc: expiresUtc,
            oneTimeKeyCount: 3,
            cancellationToken: CancellationToken.None);

        var simulatedPeer = sut.Peers.Single(p => p.NetworkPeerId == simulatedPeerId);
        var simulatedPkh = Percolator.Identity.IdentityPublicKeyHash.FromSpki(simulatedPeer.IdentitySigningKeySpki);

        // Act: Pop the bundle 4 times
        var pop1 = await sut.TryPopPreKeyBundleByRecipientPkhAsync(
            relayHostPeerId,
            simulatedPkh,
            cancellationToken: CancellationToken.None);
        var pop2 = await sut.TryPopPreKeyBundleByRecipientPkhAsync(
            relayHostPeerId,
            simulatedPkh,
            cancellationToken: CancellationToken.None);
        var pop3 = await sut.TryPopPreKeyBundleByRecipientPkhAsync(
            relayHostPeerId,
            simulatedPkh,
            cancellationToken: CancellationToken.None);
        var pop4 = await sut.TryPopPreKeyBundleByRecipientPkhAsync(
            relayHostPeerId,
            simulatedPkh,
            cancellationToken: CancellationToken.None);

        // Assert: Verify onetime keys are popped one at a time (checking internal state to verify behavior)
        pop1.Should().NotBeNull();
        pop2.Should().NotBeNull();
        pop3.Should().NotBeNull();
        pop4.Should().NotBeNull();

        pop1!.OneTimeKeys.Should().HaveCount(1);
        pop2!.OneTimeKeys.Should().HaveCount(1);
        pop3!.OneTimeKeys.Should().HaveCount(1);
        pop4!.OneTimeKeys.Should().HaveCount(0);

        var poppedIds = new[]
        {
            pop1.OneTimeKeys.Single().Id,
            pop2.OneTimeKeys.Single().Id,
            pop3.OneTimeKeys.Single().Id
        };
        poppedIds.Distinct().Should().HaveCount(3);

        var relay = sut.Peers.Single(p => p.NetworkPeerId == relayHostPeerId);
        relay.PublishedPreKeyBundles.Should().HaveCount(1);
        relay.PublishedPreKeyBundles.Single().OneTimeKeys.Should().HaveCount(0);
    }
}

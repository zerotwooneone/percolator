using System;
using System.Collections.Generic;
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
using NUnit.Framework;
using Percolator.Application.Configuration;
using Percolator.Cryptography;
using Percolator.Cryptography.Primitives;
using Percolator.Network;

namespace Desktop.Wpf.Tests;

[TestFixture]
public sealed class SimulatorStateServiceInitializationTests
{
    private sealed class RepositoryStub : ISimulatorStateRepository
    {
        private readonly TaskCompletionSource<SimulatorStateSnapshot> _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int LoadCalls;

        public Task<SimulatorStateSnapshot> LoadStateAsync(CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref LoadCalls);
            return _gate.Task;
        }

        public Task SaveStateAsync(SimulatorStateSnapshot snapshot, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public void Release(SimulatorStateSnapshot snapshot) => _gate.TrySetResult(snapshot);
    }

    [Test]
    public async Task InitializeAsync_CoalescesConcurrentCalls()
    {
        // Arrange
        var store = new RepositoryStub();
        var diagnostics = new SimulatorDiagnosticsService();

        var services = new ServiceCollection();
        services.AddSingleton<IClock, SystemClock>();
        var sp = services.BuildServiceProvider();
        var scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();

        var pending = new SimulatedPeerPendingInbox();
        var transportOptions = Options.Create(new TransportOptions { SimulatorPort = 5002 });
        var engine = new SignalProtocolEngine(new SystemClock());

        var sut = new SimulatorStateService(store, diagnostics, pending, scopeFactory, transportOptions, engine);

        // Act
        var initializer = (ISimulatorStateInitializer)sut;
        var t1 = initializer.InitializeAsync();
        var t2 = initializer.InitializeAsync();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        while (Volatile.Read(ref store.LoadCalls) == 0 && !cts.IsCancellationRequested)
        {
            await Task.Delay(5, cts.Token);
        }

        store.LoadCalls.Should().Be(1);

        store.Release(new SimulatorStateSnapshot(
            Version: 1,
            Peers: Array.Empty<PeerStateSnapshot>(),
            Relationships: Array.Empty<PeerRelationshipSnapshot>(),
            Relays: Array.Empty<RelayStateSnapshot>(),
            Groups: Array.Empty<GroupConversationDto>()));
        await Task.WhenAll(t1, t2);

        // Assert
        store.LoadCalls.Should().Be(1);
    }

    [Test]
    public async Task InitializeAsync_RestoresSessionsFromSnapshot()
    {
        // Arrange
        var peerId = new Percolator.Network.PeerId(Guid.NewGuid());
        var remotePeerId = new Percolator.Cryptography.Primitives.PeerId(Guid.NewGuid());
        var signedPreKeyId = Guid.NewGuid();

        using var identity = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var priv = identity.ExportECPrivateKey();
        var spki = identity.ExportSubjectPublicKeyInfo();

        var model = new SimulatedPeerModel(
            peerId: peerId,
            selfIdentityId: 99000,
            displayName: "Alice",
            isOnline: true,
            isRelayCapable: false,
            identitySigningKeySpki: spki,
            identitySigningKeyPrivateKeyEcPrivateKey: priv);

        // Add signed prekey
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

        // Add pending handshake response
        var responseCorrelationId = Guid.NewGuid();
        model.PendingInviteHandshakeResponsesMutable.Add(new SimulatedPendingInviteHandshakeResponseModel(
            responseCorrelationId,
            new byte[] { 20, 21, 22 }));

        var snapshot = new SimulatorStateSnapshot(
            Version: 1,
            Peers: new[] { model.Freeze() },
            Relationships: Array.Empty<PeerRelationshipSnapshot>(),
            Relays: Array.Empty<RelayStateSnapshot>(),
            Groups: Array.Empty<GroupConversationDto>());

        var store = new RepositoryStub();
        store.Release(snapshot);

        var diagnostics = new SimulatorDiagnosticsService();
        var services = new ServiceCollection();
        services.AddSingleton<IClock, SystemClock>();
        var sp = services.BuildServiceProvider();
        var scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();
        var pending = new SimulatedPeerPendingInbox();
        var transportOptions = Options.Create(new TransportOptions { SimulatorPort = 5002 });
        var engine = new SignalProtocolEngine(new SystemClock());

        var sut = new SimulatorStateService(store, diagnostics, pending, scopeFactory, transportOptions, engine);

        // Act
        var initializer = (ISimulatorStateInitializer)sut;
        await initializer.InitializeAsync();

        // Assert
        var restoredPeer = sut.Peers.FirstOrDefault(p => p.PeerId == peerId);
        restoredPeer.Should().NotBeNull();

        // Verify signed prekeys were restored
        restoredPeer.SignedPreKeysMutable.Should().HaveCount(1);
        restoredPeer.SignedPreKeysMutable.First().SignedPreKeyId.Should().Be(signedPreKeyId);

        // Verify session was restored
        restoredPeer.SessionsMutable.Should().HaveCount(1);
        restoredPeer.SessionsMutable.ContainsKey(session.Id).Should().BeTrue();

        // Verify outbound invite was restored
        restoredPeer.OutboundInvitesMutable.Should().HaveCount(1);
        restoredPeer.OutboundInvitesMutable.First().CorrelationId.Should().Be(inviteCorrelationId);

        // Verify pending handshake response was restored
        restoredPeer.PendingInviteHandshakeResponsesMutable.Should().HaveCount(1);
        restoredPeer.PendingInviteHandshakeResponsesMutable.First().CorrelationId.Should().Be(responseCorrelationId);
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

        var pending = new SimulatedPeerPendingInbox();
        var diagnostics = new SimulatorDiagnosticsService();
        var transportOptions = Options.Create(new TransportOptions { SimulatorPort = 5002 });
        var engine = new SignalProtocolEngine(new SystemClock());

        var store = new RepositoryStub();

        var sut = new SimulatorStateService(
            store,
            diagnostics,
            pending,
            scopeFactory,
            transportOptions,
            engine);

        // Release empty snapshot so InitializeAsync can complete
        store.Release(new SimulatorStateSnapshot(
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

        var simulatedPeer = sut.Peers.Single(p => p.PeerId == simulatedPeerId);
        var simulatedPkh = SHA256.HashData(simulatedPeer.IdentitySigningKeySpki);

        // Act: pop the bundle 4 times
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

        // Assert
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

        var relay = sut.Peers.Single(p => p.PeerId == relayHostPeerId);
        relay.PublishedPreKeyBundles.Should().HaveCount(1);
        relay.PublishedPreKeyBundles.Single().OneTimeKeys.Should().HaveCount(0);
    }
}

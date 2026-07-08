using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Desktop.Wpf.Features.Simulator;
using Desktop.Wpf.Features.Simulator.Models;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NUnit.Framework;
using Percolator.Application.Configuration;
using Percolator.Cryptography;
using Percolator.Identity;

namespace Desktop.Wpf.Tests;

[TestFixture]
public sealed class SimulatorStateStoreTests
{
    [Test]
    public async Task SaveLoad_RoundTrip_Works()
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"percolator-sim-{Guid.NewGuid():N}.json");
        try
        {
            var peerId = new Percolator.Network.NetworkPeerId(Guid.NewGuid());

            var services = new ServiceCollection();
            services.AddSingleton<IClock>(new TestClock(DateTimeOffset.UtcNow));
            var sp = services.BuildServiceProvider();
            var scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();

            var options = Options.Create(new TransportOptions { SimulatorPort = 5002 });
            var keys = new SimulatedPeerKeyFactory();

            var store = new JsonSimulatorStateRepository(
                overridePath: tmp,
                transportOptions: options,
                keys: keys,
                scopeFactory: scopeFactory);

            var remotePeerId = new Percolator.Cryptography.Primitives.PeerId(Guid.NewGuid());
            var signedPreKeyId = Guid.NewGuid();

            using var identity = System.Security.Cryptography.ECDiffieHellman.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP256);
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

            var host1 = new Percolator.Network.NetworkPeerId(Guid.NewGuid());
            var host2 = new Percolator.Network.NetworkPeerId(Guid.NewGuid());
            var host3 = new Percolator.Network.NetworkPeerId(Guid.NewGuid());

            model.SignedPreKeysMutable.Add(new SimulatedSignedPreKeyModel(
                SignedPreKeyId: signedPreKeyId,
                PrivateEcPrivateKey: new byte[] { 1, 2, 3 },
                PublicSpki: new byte[] { 4, 5, 6 }));

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

            model.SignedPreKeysMutable.Add(new SimulatedSignedPreKeyModel(Guid.NewGuid(), RandomNumberGenerator.GetBytes(32), RandomNumberGenerator.GetBytes(32)));
            model.SessionsMutable[session.Id] = session;

            var inviterPeerId = new Percolator.Network.NetworkPeerId(Guid.NewGuid());
            model.PendingInboundDirectInvitesMutable.Add(new SimulatedPendingInboundDirectInviteModel(
                CorrelationId: Guid.NewGuid(),
                RequestBytes: new byte[] { 0x10, 0x11, 0x12 },
                ReceivedAtUtc: DateTimeOffset.UtcNow,
                InviterIdentityKeySpki: new byte[] { 0x20, 0x21 },
                InviterNetworkPeerId: inviterPeerId));

            var relationships = new[]
            {
                new PeerRelationshipSnapshot(model.NetworkPeerId, host1, RelationshipType.PublishedKey),
                new PeerRelationshipSnapshot(model.NetworkPeerId, host2, RelationshipType.PublishedKey),
                new PeerRelationshipSnapshot(model.NetworkPeerId, host3, RelationshipType.PublishedKey)
            };

            var relayHostPeerId = new Percolator.Network.NetworkPeerId(Guid.NewGuid());
            var ackUp = Guid.NewGuid();
            var ackDown = Guid.NewGuid();
            var targetPkh = IdentityPublicKeyHash.FromSpki(Guid.NewGuid().ToByteArray());
            var enqueuedUtc = DateTimeOffset.UtcNow;

            var relays = new[]
            {
                new RelayStateSnapshot(
                    RelayHostNetworkPeerId: relayHostPeerId,
                    UpstreamToMain: new[]
                    {
                        new OutboundRelayMessageSnapshot(
                            AckId: ackUp,
                            OpaqueBytes: new byte[] { 0x0A, 0x0B },
                            EnqueuedUtc: enqueuedUtc,
                            DebugType: "up")
                    },
                    DownstreamToPeers: new[]
                    {
                        new InboundRelayMessageSnapshot(
                            AckId: ackDown,
                            TargetIdentityPublicKeyHash: targetPkh,
                            OpaqueBytes: new byte[] { 0x0C, 0x0D },
                            EnqueuedUtc: enqueuedUtc,
                            DebugType: "down")
                    })
            };

            var groups = new[]
            {
                new GroupConversationDto
                {
                    Version = 1,
                    GroupId = Guid.NewGuid(),
                    Name = "g",
                    ParticipantPeerIds = new() { peerId.Value, host1.Value }
                }
            };

            var snapshot = new SimulatorStateSnapshot(
                Version: 1,
                Peers: new[] { model.Freeze() },
                Relationships: relationships,
                Relays: relays,
                Groups: groups);

            await store.SaveStateAsync(snapshot, CancellationToken.None);

            var loadedSnapshot = await store.LoadStateAsync(CancellationToken.None);
            loadedSnapshot.Peers.Should().HaveCount(1);
            var loadedPeer = loadedSnapshot.Peers.Single();

            loadedPeer.NetworkPeerId.Should().Be(peerId);
            loadedPeer.DisplayName.Should().Be("Alice");
            loadedPeer.SignedPreKeys.Should().HaveCount(2);
            loadedPeer.Sessions.Should().HaveCount(1);

            loadedPeer.PendingInboundDirectInvites.Should().HaveCount(1);
            loadedPeer.PendingInboundDirectInvites.Single().InviterNetworkPeerId.Should().Be(inviterPeerId);

            loadedSnapshot.Relationships
                .Count(r => r.SourceNetworkPeerId == model.NetworkPeerId && r.Type == RelationshipType.PublishedKey)
                .Should()
                .Be(3);

            loadedSnapshot.Relays.Should().HaveCount(1);
            loadedSnapshot.Relays.Single().RelayHostNetworkPeerId.Should().Be(relayHostPeerId);
            loadedSnapshot.Relays.Single().UpstreamToMain.Should().ContainSingle(m => m.AckId == ackUp && m.DebugType == "up");
            loadedSnapshot.Relays.Single().DownstreamToPeers.Should().ContainSingle(m => m.AckId == ackDown && m.DebugType == "down");
            loadedSnapshot.Relays.Single().DownstreamToPeers.Single(m => m.AckId == ackDown).TargetIdentityPublicKeyHash.Should().Be(targetPkh);

            loadedSnapshot.Groups.Should().HaveCount(1);
            loadedSnapshot.Groups.Single().GroupId.Should().Be(groups[0].GroupId);
            loadedSnapshot.Groups.Single().Name.Should().Be("g");
            loadedSnapshot.Groups.Single().ParticipantPeerIds.Should().Contain(peerId.Value);
        }
        finally
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
        }
    }

    [Test]
    public async Task PendingInboundDirectInvite_WithInviterPeerId_PersistsCorrectly()
    {
        // B6.1/B8: Verify that pending inbound direct invites persist inviter peer id correctly
        var tmp = Path.Combine(Path.GetTempPath(), $"percolator-sim-{Guid.NewGuid():N}.json");
        try
        {
            var peerId = new Percolator.Network.NetworkPeerId(Guid.NewGuid());
            var inviterPeerId = new Percolator.Network.NetworkPeerId(Guid.NewGuid());
            var correlationId = Guid.NewGuid();

            var services = new ServiceCollection();
            services.AddSingleton<IClock>(new TestClock(DateTimeOffset.UtcNow));
            var sp = services.BuildServiceProvider();
            var scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();

            var options = Options.Create(new TransportOptions { SimulatorPort = 5002 });
            var keys = new SimulatedPeerKeyFactory();

            var store = new JsonSimulatorStateRepository(
                overridePath: tmp,
                transportOptions: options,
                keys: keys,
                scopeFactory: scopeFactory);

            using var identity = System.Security.Cryptography.ECDiffieHellman.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP256);
            var priv = identity.ExportECPrivateKey();
            var spki = identity.ExportSubjectPublicKeyInfo();

            var model = new SimulatedPeerModel(
                networkPeerId: peerId,
                selfIdentityId: 99000,
                displayName: "TestPeer",
                isRelayCapable: false,
                identitySigningKeySpki: spki,
                identitySigningKeyPrivateKeyEcPrivateKey: priv,
                endpoint: new System.Net.DnsEndPoint("127.0.0.1", 5002));

            // Add a pending inbound direct invite with inviter peer id
            model.PendingInboundDirectInvitesMutable.Add(new SimulatedPendingInboundDirectInviteModel(
                CorrelationId: correlationId,
                RequestBytes: new byte[] { 0x01, 0x02, 0x03 },
                ReceivedAtUtc: DateTimeOffset.UtcNow,
                InviterIdentityKeySpki: new byte[] { 0x10, 0x11 },
                InviterNetworkPeerId: inviterPeerId));

            var snapshot = new SimulatorStateSnapshot(
                Version: 1,
                Peers: new[] { model.Freeze() },
                Relationships: Array.Empty<PeerRelationshipSnapshot>(),
                Relays: Array.Empty<RelayStateSnapshot>(),
                Groups: Array.Empty<GroupConversationDto>());

            await store.SaveStateAsync(snapshot, CancellationToken.None);

            var loadedSnapshot = await store.LoadStateAsync(CancellationToken.None);
            loadedSnapshot.Peers.Should().HaveCount(1);
            var loadedPeer = loadedSnapshot.Peers.Single();

            loadedPeer.PendingInboundDirectInvites.Should().HaveCount(1);
            var loadedInvite = loadedPeer.PendingInboundDirectInvites.Single();
            loadedInvite.CorrelationId.Should().Be(correlationId);
            loadedInvite.InviterNetworkPeerId.Should().Be(inviterPeerId);
            loadedInvite.InviterIdentityKeySpki.Should().BeEquivalentTo(new byte[] { 0x10, 0x11 });
            loadedInvite.RequestBytes.Should().BeEquivalentTo(new byte[] { 0x01, 0x02, 0x03 });
        }
        finally
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
        }
    }
}

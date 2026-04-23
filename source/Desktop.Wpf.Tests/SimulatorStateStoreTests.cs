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
using Percolator.Cryptography.Primitives;
using Percolator.Identity;
using Percolator.Network;

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
            var peerId = new Percolator.Network.PeerId(Guid.NewGuid());

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
                peerId: peerId,
                selfIdentityId: 99000,
                displayName: "Alice",
                isOnline: true,
                isRelayCapable: false,
                identitySigningKeySpki: spki,
                identitySigningKeyPrivateKeyEcPrivateKey: priv);

            var host1 = new Percolator.Network.PeerId(Guid.NewGuid());
            var host2 = new Percolator.Network.PeerId(Guid.NewGuid());
            var host3 = new Percolator.Network.PeerId(Guid.NewGuid());

            model.SignedPreKeysMutable.Add(new SimulatedSignedPreKeyModel(
                SignedPreKeyId: signedPreKeyId,
                PrivateEcPrivateKey: new byte[] { 1, 2, 3 },
                PublicSpki: new byte[] { 4, 5, 6 }));

            var ratchet = new RatchetState(
                rootKey: new RootKey(new byte[] { 9, 9, 9 }),
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

            var relationships = new[]
            {
                new PeerRelationshipSnapshot(model.PeerId, host1, RelationshipType.PublishedKey),
                new PeerRelationshipSnapshot(model.PeerId, host2, RelationshipType.PublishedKey),
                new PeerRelationshipSnapshot(model.PeerId, host3, RelationshipType.PublishedKey)
            };

            var relayHostPeerId = new Percolator.Network.PeerId(Guid.NewGuid());
            var ackUp = Guid.NewGuid();
            var ackDown = Guid.NewGuid();
            var targetPkh = IdentityPublicKeyHash.FromSpki(Guid.NewGuid().ToByteArray());
            var enqueuedUtc = DateTimeOffset.UtcNow;

            var relays = new[]
            {
                new RelayStateSnapshot(
                    RelayHostPeerId: relayHostPeerId,
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

            loadedPeer.PeerId.Should().Be(peerId);
            loadedPeer.DisplayName.Should().Be("Alice");
            loadedPeer.SignedPreKeys.Should().HaveCount(2);
            loadedPeer.Sessions.Should().HaveCount(1);

            loadedSnapshot.Relationships
                .Count(r => r.SourcePeerId == model.PeerId && r.Type == RelationshipType.PublishedKey)
                .Should()
                .Be(3);

            loadedSnapshot.Relays.Should().HaveCount(1);
            loadedSnapshot.Relays.Single().RelayHostPeerId.Should().Be(relayHostPeerId);
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
}

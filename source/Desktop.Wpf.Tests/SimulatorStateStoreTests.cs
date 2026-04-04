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
            var peerId = Guid.NewGuid();

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

            var remotePeerId = Guid.NewGuid();
            var signedPreKeyId = Guid.NewGuid();

            using var identity = System.Security.Cryptography.ECDiffieHellman.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP256);
            var priv = identity.ExportECPrivateKey();
            var spki = identity.ExportSubjectPublicKeyInfo();

            var model = new SimulatedPeerModel(
                peerId: peerId,
                displayName: "Alice",
                isOnline: true,
                isRelayCapable: false,
                identitySigningKeySpki: spki,
                identitySigningKeyPrivateKeyEcPrivateKey: priv);

            var host1 = Guid.NewGuid();
            var host2 = Guid.NewGuid();
            var host3 = Guid.NewGuid();

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
                remotePeerId: new PeerId(remotePeerId),
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

            await store.SavePeersAsync(new[] { model.Freeze() }, relationships, CancellationToken.None);

            var loaded = await store.LoadPeersAsync(CancellationToken.None);
            loaded.Should().HaveCount(1);
            var loadedPeer = loaded.Single();
            loadedPeer.PeerId.Should().Be(peerId);
            loadedPeer.DisplayName.CurrentValue.Should().Be("Alice");
            loadedPeer.SignedPreKeys.Should().HaveCount(2);
            loadedPeer.Sessions.Count.Should().Be(1);

            var loadedRelationships = await store.LoadRelationshipsAsync(CancellationToken.None);
            loadedRelationships
                .Count(r => r.SourcePeerId == model.PeerId && r.Type == RelationshipType.PublishedKey)
                .Should()
                .Be(3);
        }
        finally
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
        }
    }
}

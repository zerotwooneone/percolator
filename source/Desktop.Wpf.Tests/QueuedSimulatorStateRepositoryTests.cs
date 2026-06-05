using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
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

namespace Desktop.Wpf.Tests;

[TestFixture]
public sealed class QueuedSimulatorStateRepositoryTests
{
    [Test]
    public async Task ConcurrentSaves_NoDeadlocks_SingleWriterBehavior()
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"percolator-sim-{Guid.NewGuid():N}.json");
        try
        {
            var services = new ServiceCollection();
            services.AddSingleton<IClock>(new TestClock(DateTimeOffset.UtcNow));
            var sp = services.BuildServiceProvider();
            var scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();

            var options = Options.Create(new TransportOptions { SimulatorPort = 5002 });
            var keys = new SimulatedPeerKeyFactory();

            var innerRepo = new JsonSimulatorStateRepository(
                overridePath: tmp,
                transportOptions: options,
                keys: keys,
                scopeFactory: scopeFactory);

            using var queuedRepo = new QueuedSimulatorStateRepository(innerRepo);

            // Create a snapshot
            var peerId = new Percolator.Network.PeerId(Guid.NewGuid());
            using var identity = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
            var priv = identity.ExportECPrivateKey();
            var spki = identity.ExportSubjectPublicKeyInfo();

            var model = new SimulatedPeerModel(
                peerId: peerId,
                selfIdentityId: 99000,
                displayName: "TestPeer",
                isRelayCapable: false,
                identitySigningKeySpki: spki,
                identitySigningKeyPrivateKeyEcPrivateKey: priv,
                endpoint: new DnsEndPoint("127.77.1.1", 5002));

            var snapshot = new SimulatorStateSnapshot(
                Version: 1,
                Peers: new[] { model.Freeze() },
                Relationships: Array.Empty<PeerRelationshipSnapshot>(),
                Relays: Array.Empty<RelayStateSnapshot>(),
                Groups: Array.Empty<GroupConversationDto>());

            // Trigger multiple concurrent save requests
            var tasks = Enumerable.Range(0, 10).Select(async _ =>
            {
                await queuedRepo.SaveStateAsync(snapshot, CancellationToken.None).ConfigureAwait(false);
            }).ToArray();

            // All saves should complete without deadlocks
            await Task.WhenAll(tasks).ConfigureAwait(false);

            // Verify the file was saved
            var loadedSnapshot = await queuedRepo.LoadStateAsync(CancellationToken.None).ConfigureAwait(false);
            loadedSnapshot.Peers.Should().HaveCount(1);
            loadedSnapshot.Peers.Single().PeerId.Should().Be(peerId);
        }
        finally
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
            try { if (File.Exists(tmp + ".tmp")) File.Delete(tmp + ".tmp"); } catch { }
        }
    }

    [Test]
    public async Task ConcurrentLoadAndSaves_NoDeadlocks()
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"percolator-sim-{Guid.NewGuid():N}.json");
        try
        {
            var services = new ServiceCollection();
            services.AddSingleton<IClock>(new TestClock(DateTimeOffset.UtcNow));
            var sp = services.BuildServiceProvider();
            var scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();

            var options = Options.Create(new TransportOptions { SimulatorPort = 5002 });
            var keys = new SimulatedPeerKeyFactory();

            var innerRepo = new JsonSimulatorStateRepository(
                overridePath: tmp,
                transportOptions: options,
                keys: keys,
                scopeFactory: scopeFactory);

            using var queuedRepo = new QueuedSimulatorStateRepository(innerRepo);

            // Create and save initial snapshot
            var peerId = new Percolator.Network.PeerId(Guid.NewGuid());
            using var identity = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
            var priv = identity.ExportECPrivateKey();
            var spki = identity.ExportSubjectPublicKeyInfo();

            var model = new SimulatedPeerModel(
                peerId: peerId,
                selfIdentityId: 99000,
                displayName: "TestPeer",
                isRelayCapable: false,
                identitySigningKeySpki: spki,
                identitySigningKeyPrivateKeyEcPrivateKey: priv,
                endpoint: new DnsEndPoint("127.77.1.1", 5002));

            var snapshot = new SimulatorStateSnapshot(
                Version: 1,
                Peers: new[] { model.Freeze() },
                Relationships: Array.Empty<PeerRelationshipSnapshot>(),
                Relays: Array.Empty<RelayStateSnapshot>(),
                Groups: Array.Empty<GroupConversationDto>());

            await queuedRepo.SaveStateAsync(snapshot, CancellationToken.None).ConfigureAwait(false);

            // Trigger concurrent load and save operations
            var tasks = new List<Task>();
            for (int i = 0; i < 5; i++)
            {
                tasks.Add(queuedRepo.LoadStateAsync(CancellationToken.None));
                tasks.Add(queuedRepo.SaveStateAsync(snapshot, CancellationToken.None));
            }

            // All operations should complete without deadlocks
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
        finally
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
            try { if (File.Exists(tmp + ".tmp")) File.Delete(tmp + ".tmp"); } catch { }
        }
    }
}

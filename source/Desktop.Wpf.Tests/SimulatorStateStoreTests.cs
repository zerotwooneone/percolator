using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Desktop.Wpf.Features.Simulator;
using FluentAssertions;
using NUnit.Framework;

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
            var store = new JsonSimulatorStateRepository(overridePath: tmp);
            var state = new SimulatorStateDto
            {
                Version = 1
            };
            var peerId = Guid.NewGuid();
            state.Peers.Add(new SimulatedPeerDto
            {
                PeerId = peerId,
                DisplayName = "Alice",
                IsOnline = true,
                PublishedKeysToPeerIds = { Guid.NewGuid(), Guid.NewGuid() },
                RuntimeStore = new SimulatedPeerRuntimeStoreDto
                {
                    Version = 1,
                    SignedPreKeys =
                    {
                        new SimulatedSignedPreKeyDto
                        {
                            SignedPreKeyId = Guid.NewGuid(),
                            PrivateEcPrivateKey = new byte[] { 1, 2, 3 },
                            PublicSpki = new byte[] { 4, 5, 6 }
                        }
                    },
                    Sessions =
                    {
                        new SimulatedSecureSessionDto
                        {
                            SessionId = Guid.NewGuid(),
                            RemotePeerId = Guid.NewGuid(),
                            ProtocolVersion = 1,
                            RootKey = new byte[] { 9, 9, 9 },
                            SendCounter = 7,
                            RecvCounter = 8,
                            PrevChainLength = 0,
                            CreatedAtUtc = DateTimeOffset.UtcNow,
                            LastUsedAtUtc = DateTimeOffset.UtcNow
                        }
                    }
                }
            });

            await store.SaveAsync(state, CancellationToken.None);

            var loaded = await store.LoadAsync(CancellationToken.None);
            loaded.Should().NotBeNull();
            loaded!.Peers.Should().HaveCount(1);
            loaded.Peers[0].DisplayName.Should().Be("Alice");
            loaded.Peers[0].PeerId.Should().Be(peerId);
            loaded.Peers[0].PublishedKeysToPeerIds.Should().HaveCount(2);
            loaded.Peers[0].RuntimeStore.Should().NotBeNull();
            loaded.Peers[0].RuntimeStore.SignedPreKeys.Should().HaveCount(1);
            loaded.Peers[0].RuntimeStore.Sessions.Should().HaveCount(1);
        }
        finally
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
        }
    }
}

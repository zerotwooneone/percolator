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
            var store = new JsonSimulatorStateStore(TimeSpan.FromMilliseconds(1), overridePath: tmp);
            var state = new SimulatorStateDto
            {
                Version = 1
            };
            state.Peers.Add(new SimulatedPeerDto
            {
                PeerId = Guid.NewGuid(),
                DisplayName = "Alice",
                IsOnline = true
            });

            await store.SaveAsync(state, CancellationToken.None);

            var loaded = await store.LoadAsync(CancellationToken.None);
            loaded.Should().NotBeNull();
            loaded!.Peers.Should().HaveCount(1);
            loaded.Peers[0].DisplayName.Should().Be("Alice");
        }
        finally
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
        }
    }
}

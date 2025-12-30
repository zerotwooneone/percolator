using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Desktop.Wpf.Features.Simulator;
using FluentAssertions;
using NUnit.Framework;
using Percolator.Cryptography;
using Percolator.Cryptography.Primitives;

namespace Desktop.Wpf.Tests;

[TestFixture]
public sealed class HandshakeSimulatorViewModelTests
{
    private sealed class FakeSim : IPendingHandshakeSimulatorService
    {
        private readonly Dictionary<Guid, SimulatedPeerState> _states = new();

        public Task<RequestCorrelationId> AddSyntheticPendingAsync(string? displayName = null, CancellationToken ct = default)
        {
            var id = new RequestCorrelationId(Guid.NewGuid());
            _states[id.Value] = SimulatedPeerState.PendingInvite;
            return Task.FromResult(id);
        }

        public Task<IReadOnlyList<RequestCorrelationId>> AddSyntheticPendingsAsync(int count, CancellationToken ct = default)
        {
            var list = new List<RequestCorrelationId>(count);
            for (var i = 0; i < count; i++)
            {
                var id = new RequestCorrelationId(Guid.NewGuid());
                _states[id.Value] = SimulatedPeerState.PendingInvite;
                list.Add(id);
            }
            return Task.FromResult((IReadOnlyList<RequestCorrelationId>)list);
        }

        public IReadOnlyList<SimulatedPeerSnapshot> SnapshotPeers()
        {
            return _states
                .Select(kv => new SimulatedPeerSnapshot(new RequestCorrelationId(kv.Key), kv.Value, DisplayName: null))
                .ToList();
        }

        public int CorrelateOutboundSnapshot()
        {
            // Advance exactly one pending peer to observed, deterministic by guid order
            var next = _states.Keys.OrderBy(k => k).FirstOrDefault(k => _states[k] == SimulatedPeerState.PendingInvite);
            if (next == Guid.Empty) return 0;
            _states[next] = SimulatedPeerState.OutboundResponseObserved;
            return 1;
        }
    }

    [Test]
    public async Task SimulateManyCommand_CreatesNPeers_AndExposesSnapshots()
    {
        var sim = new FakeSim();
        var vm = new HandshakeSimulatorViewModel(sim)
        {
            PeerCount = 3
        };

        vm.SimulateManyCommand.Execute(null);
        await Task.Delay(50);

        vm.Peers.Should().HaveCount(3);
        vm.Status.Should().Contain("Created 3");
    }

    [Test]
    public async Task CorrelateOutboundCommand_AdvancesOnePeerState()
    {
        var sim = new FakeSim();
        var vm = new HandshakeSimulatorViewModel(sim)
        {
            PeerCount = 2
        };

        vm.SimulateManyCommand.Execute(null);
        await Task.Delay(50);

        vm.Peers.Count(p => p.State == SimulatedPeerState.PendingInvite).Should().Be(2);

        vm.CorrelateOutboundCommand.Execute(null);
        await Task.Delay(50);

        vm.Peers.Count(p => p.State == SimulatedPeerState.OutboundResponseObserved).Should().Be(1);
        vm.Peers.Count(p => p.State == SimulatedPeerState.PendingInvite).Should().Be(1);
        vm.Status.Should().Contain("Correlated 1");
    }
}

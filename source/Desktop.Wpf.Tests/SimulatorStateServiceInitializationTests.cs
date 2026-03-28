using System;
using System.Collections.Generic;
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

namespace Desktop.Wpf.Tests;

[TestFixture]
public sealed class SimulatorStateServiceInitializationTests
{
    private sealed class RepositoryStub : ISimulatorStateRepository
    {
        private readonly TaskCompletionSource<IReadOnlyList<SimulatedPeerModel>> _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public SimulatedRelayModel? Relay { get; set; }

        public int LoadCalls;

        public Task<IReadOnlyList<SimulatedPeerModel>> LoadPeersAsync(CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref LoadCalls);
            return _gate.Task;
        }

        public Task SavePeersAsync(IReadOnlyList<PeerStateSnapshot> peers, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<SimulatedRelayModel?> LoadRelayAsync(Guid relayHostPeerId, CancellationToken cancellationToken = default)
            => Task.FromResult(Relay);

        public Task SaveRelayAsync(RelayStateSnapshot relay, CancellationToken cancellationToken = default)
        {
            Relay = new SimulatedRelayModel(relay.RelayHostPeerId);
            return Task.CompletedTask;
        }

        public void Release(IReadOnlyList<SimulatedPeerModel> peers) => _gate.TrySetResult(peers);
    }

    [Test]
    public async Task InitializeAsync_CoalescesConcurrentCalls()
    {
        // Arrange
        var store = new RepositoryStub();
        var options = Options.Create(new TransportOptions { GrpcPort = 5002 });
        var diagnostics = new SimulatorDiagnosticsService();

        var services = new ServiceCollection();
        services.AddSingleton<IClock, SystemClock>();
        var sp = services.BuildServiceProvider();
        var scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();

        var pending = new SimulatedPeerPendingInbox();
        var engine = new SignalProtocolEngine(new SystemClock());

        var sut = new SimulatorStateService(store, options, diagnostics, pending, scopeFactory, engine);

        // Act
        var t1 = sut.InitializeAsync();
        var t2 = sut.InitializeAsync();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        while (Volatile.Read(ref store.LoadCalls) == 0 && !cts.IsCancellationRequested)
        {
            await Task.Delay(5, cts.Token);
        }

        store.LoadCalls.Should().Be(1);

        store.Release(Array.Empty<SimulatedPeerModel>());
        await Task.WhenAll(t1, t2);

        // Assert
        store.LoadCalls.Should().Be(1);
    }
}

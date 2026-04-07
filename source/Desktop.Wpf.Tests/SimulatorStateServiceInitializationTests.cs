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
}

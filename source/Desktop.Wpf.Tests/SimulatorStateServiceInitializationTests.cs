using System;
using System.Threading;
using System.Threading.Tasks;
using Desktop.Wpf.Features.Simulator;
using FluentAssertions;
using Microsoft.Extensions.Options;
using NUnit.Framework;
using Percolator.Application.Configuration;

namespace Desktop.Wpf.Tests;

[TestFixture]
public sealed class SimulatorStateServiceInitializationTests
{
    private sealed class StoreStub : ISimulatorStateStore
    {
        private readonly TaskCompletionSource<SimulatorStateDto?> _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int LoadCalls;

        public Task<SimulatorStateDto?> LoadAsync(CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref LoadCalls);
            return _gate.Task;
        }

        public Task SaveAsync(SimulatorStateDto state, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public void Release(SimulatorStateDto? state) => _gate.TrySetResult(state);
    }

    [Test]
    public async Task InitializeAsync_CoalescesConcurrentCalls()
    {
        // Arrange
        var store = new StoreStub();
        var keys = new SimulatedPeerKeyFactory();
        var options = Options.Create(new TransportOptions { SimulatorPort = 5002 });
        var diagnostics = new SimulatorDiagnosticsService();
        var sut = new SimulatorStateService(store, keys, options, diagnostics);

        // Act
        var t1 = sut.InitializeAsync();
        var t2 = sut.InitializeAsync();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        while (Volatile.Read(ref store.LoadCalls) == 0 && !cts.IsCancellationRequested)
        {
            await Task.Delay(5, cts.Token);
        }

        store.LoadCalls.Should().Be(1);

        store.Release(new SimulatorStateDto { Version = 1 });
        await Task.WhenAll(t1, t2);

        // Assert
        store.LoadCalls.Should().Be(1);
    }
}

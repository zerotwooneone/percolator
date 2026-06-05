using System;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Desktop.Wpf.Features.Simulator;
using FluentAssertions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NUnit.Framework;
using Percolator.Application.Configuration;
using Percolator.Cryptography;
using Desktop.Wpf.Features.Simulator.Protocol;
using Microsoft.Extensions.DependencyInjection;

namespace Desktop.Wpf.Tests;

[TestFixture]
public sealed class SimulatorStateServiceEndpointResolutionTests
{
    [Test]
    public async Task TryResolvePeerId_ExistingEndpoint_ReturnsPeerId()
    {
        // Arrange
        var stateService = CreateSut();
        var initializer = (ISimulatorStateInitializer)stateService;
        await initializer.InitializeAsync(CancellationToken.None).ConfigureAwait(false);

        // Add a peer with a specific endpoint
        var peerId = await stateService.AddPeerAsync("TestPeer", CancellationToken.None).ConfigureAwait(false);
        
        // Get the peer to check its endpoint (necessary since no public API to get assigned endpoint)
        var peer = stateService.Peers.FirstOrDefault(p => p.PeerId == peerId);
        peer.Should().NotBeNull();
        var endpoint = peer!.Endpoint.CurrentValue;
        endpoint.Should().NotBeNull();

        // Act & Assert: Try to resolve the endpoint
        var resolved = stateService.TryResolvePeerId(endpoint!, out var resolvedPeerId);
        resolved.Should().BeTrue();
        resolvedPeerId.Should().Be(peerId);
    }

    [Test]
    public async Task TryResolvePeerId_NonExistingEndpoint_ReturnsFalse()
    {
        // Arrange
        var stateService = CreateSut();
        var initializer = (ISimulatorStateInitializer)stateService;
        await initializer.InitializeAsync(CancellationToken.None).ConfigureAwait(false);

        // Act & Assert: Try to resolve a non-existing endpoint
        var nonExistingEndpoint = new DnsEndPoint("127.77.255.255", 9999);
        var resolved = stateService.TryResolvePeerId(nonExistingEndpoint, out var resolvedPeerId);
        resolved.Should().BeFalse();
        resolvedPeerId.Should().Be(default);
    }

    [Test]
    public async Task TryResolvePeerId_AfterEndpointChange_ReflectsNewEndpoint()
    {
        // Arrange
        var stateService = CreateSut();
        var initializer = (ISimulatorStateInitializer)stateService;
        await initializer.InitializeAsync(CancellationToken.None).ConfigureAwait(false);

        // Add a peer
        var peerId = await stateService.AddPeerAsync("TestPeer", CancellationToken.None).ConfigureAwait(false);
        
        // Get the peer and its initial endpoint (necessary since no public API to get assigned endpoint)
        var peer = stateService.Peers.FirstOrDefault(p => p.PeerId == peerId);
        peer.Should().NotBeNull();
        var oldEndpoint = peer!.Endpoint.CurrentValue;
        oldEndpoint.Should().NotBeNull();

        // Verify old endpoint resolves
        stateService.TryResolvePeerId(oldEndpoint!, out var resolvedPeerId).Should().BeTrue();
        resolvedPeerId.Should().Be(peerId);

        // Change the endpoint (using peer model API since no public alternative)
        var newEndpoint = new DnsEndPoint("127.77.99.99", 6000);
        peer!.SetConnection(ConnectionMode.Direct, newEndpoint, peer.RelayPeerId.CurrentValue);

        // Act & Assert: Old endpoint should no longer resolve, new endpoint should resolve
        stateService.TryResolvePeerId(oldEndpoint!, out _).Should().BeFalse();
        stateService.TryResolvePeerId(newEndpoint, out resolvedPeerId).Should().BeTrue();
        resolvedPeerId.Should().Be(peerId);
    }

    [Test]
    public async Task TryResolvePeerId_AfterPeerRemoval_OldEndpointNoLongerResolves()
    {
        // Arrange
        var stateService = CreateSut();
        var initializer = (ISimulatorStateInitializer)stateService;
        await initializer.InitializeAsync(CancellationToken.None).ConfigureAwait(false);

        // Add a peer
        var peerId = await stateService.AddPeerAsync("TestPeer", CancellationToken.None).ConfigureAwait(false);
        
        // Get the peer and its endpoint (necessary since no public API to get assigned endpoint)
        var peer = stateService.Peers.FirstOrDefault(p => p.PeerId == peerId);
        peer.Should().NotBeNull();
        var endpoint = peer!.Endpoint.CurrentValue;
        endpoint.Should().NotBeNull();

        // Verify endpoint resolves
        stateService.TryResolvePeerId(endpoint!, out var resolvedPeerId).Should().BeTrue();
        resolvedPeerId.Should().Be(peerId);

        // Remove the peer
        await stateService.RemovePeerAsync(peerId, CancellationToken.None).ConfigureAwait(false);

        // Act & Assert: Endpoint should no longer resolve after peer removal
        stateService.TryResolvePeerId(endpoint!, out _).Should().BeFalse();
    }

    private static SimulatorStateService CreateSut()
    {
        var store = new InMemorySimulatorStateRepository();
        var diagnostics = new SimulatorDiagnosticsService();
        var fakeTimeProvider = new FakeTimeProvider();

        var services = new ServiceCollection();
        services.AddSingleton<IClock>(new TestClock(DateTimeOffset.UtcNow));
        var sp = services.BuildServiceProvider();
        var scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();

        var transportOptions = Options.Create(new TransportOptions { SimulatorPort = 5002 });
        var engine = new SignalProtocolEngine(new TestClock(DateTimeOffset.UtcNow));

        return new SimulatorStateService(store, diagnostics, scopeFactory, new NoopSimulatorToMainTransportService(), transportOptions, engine, fakeTimeProvider);
    }
}

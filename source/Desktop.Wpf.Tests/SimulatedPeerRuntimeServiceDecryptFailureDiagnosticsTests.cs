using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Desktop.Wpf.Features.Simulator;
using Desktop.Wpf.Features.Simulator.Protocol;
using Desktop.Wpf.Features.Sessions;
using Desktop.Wpf.Features.Simulator.Models;
using FluentAssertions;
using NUnit.Framework;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Percolator.Application.Configuration;
using Percolator.Cryptography;
using Percolator.Network;

namespace Desktop.Wpf.Tests;

[TestFixture]
public sealed class SimulatedPeerRuntimeServiceDecryptFailureDiagnosticsTests
{
    [Test]
    public async Task DecryptSessionMessageAsync_WhenDecryptThrows_EmitsDecryptFailureDiagnosticEvent()
    {
        // Arrange
        var peerId = new PeerId(Guid.NewGuid());
        var peer = CryptoTestHelpers.CreateTestPeer(
            peerId, 99000, "peer", false,
            new System.Net.DnsEndPoint("127.77.1.1", 5002));

        var diagnostics = new SimulatorDiagnosticsService();

        var repo = new InMemorySimulatorStateRepository();
        repo.Seed(new SimulatorStateSnapshot(
            Version: 1,
            Peers: new[] { peer.Freeze() },
            Relationships: Array.Empty<PeerRelationshipSnapshot>(),
            Relays: Array.Empty<RelayStateSnapshot>(),
            Groups: Array.Empty<GroupConversationDto>()));

        var services = new ServiceCollection();
        services.AddSingleton<IClock, SystemClock>();
        var sp = services.BuildServiceProvider();
        var scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();

        var transportOptions = Options.Create(new TransportOptions { SimulatorPort = 5002 });
        var engine = new SignalProtocolEngine(new SystemClock());

        var sut = new SimulatorStateService(repo, diagnostics, scopeFactory, new NoopSimulatorToMainTransportService(), transportOptions, engine);
        await ((ISimulatorStateInitializer)sut).InitializeAsync(CancellationToken.None);

        var sessionId = new SessionId(Guid.NewGuid());
        var badMessage = SessionRatchetMessage.FromBytesOwned(RandomNumberGenerator.GetBytes(10));

        // Act
        var act = async () => await sut.DecryptSessionMessageAsync(peerId, sessionId, badMessage, CancellationToken.None);

        // Assert: Exception is thrown and diagnostic event is emitted
        await act.Should().ThrowAsync<Exception>();
        diagnostics.Events.Should().Contain(e =>
            e.EventType == SimulatorDiagnosticEventType.DecryptFailure
            && e.PeerId == peerId
            && e.Message.Contains("Decrypt failure", StringComparison.Ordinal));
    }
}

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
    private sealed class InMemoryRepository : ISimulatorStateRepository
    {
        public IReadOnlyList<SimulatedPeerModel> Peers { get; set; } = Array.Empty<SimulatedPeerModel>();

        public IReadOnlyList<PeerRelationship> Relationships { get; set; } = Array.Empty<PeerRelationship>();

        public IReadOnlyList<SimulatedRelayModel> Relays { get; set; } = Array.Empty<SimulatedRelayModel>();

        public SimulatorStateSnapshot? SavedSnapshot { get; private set; }

        public Task<SimulatorStateSnapshot> LoadStateAsync(CancellationToken cancellationToken = default)
        {
            var peerSnaps = Peers.Select(p => p.Freeze()).ToList();
            var relSnaps = Relationships.Select(r => new PeerRelationshipSnapshot(r.SourcePeerId, r.TargetPeerId, r.Type)).ToList();
            var relaySnaps = Relays.Select(r => r.Freeze()).ToList();

            return Task.FromResult(new SimulatorStateSnapshot(
                Version: 1,
                Peers: peerSnaps,
                Relationships: relSnaps,
                Relays: relaySnaps,
                Groups: Array.Empty<GroupConversationDto>()));
        }

        public Task SaveStateAsync(SimulatorStateSnapshot snapshot, CancellationToken cancellationToken = default)
        {
            SavedSnapshot = snapshot;
            return Task.CompletedTask;
        }
    }

    [Test]
    public async Task DecryptSessionMessageAsync_WhenDecryptThrows_EmitsDecryptFailureDiagnosticEvent()
    {
        // Arrange
        var peerId = new PeerId(Guid.NewGuid());
        using var identityEcdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var identityPriv = identityEcdh.ExportECPrivateKey();
        using var identityEcdsa = ECDsa.Create(identityEcdh.ExportParameters(true));
        var identitySpki = identityEcdsa.ExportSubjectPublicKeyInfo();

        var diagnostics = new SimulatorDiagnosticsService();

        var repo = new InMemoryRepository
        {
            Peers = new[]
            {
                new SimulatedPeerModel(
                    peerId: peerId,
                    selfIdentityId: 99000,
                    displayName: "peer",
                    isOnline: true,
                    isRelayCapable: false,
                    identitySigningKeySpki: identitySpki,
                    identitySigningKeyPrivateKeyEcPrivateKey: identityPriv)
            }
        };

        var services = new ServiceCollection();
        services.AddSingleton<IClock, SystemClock>();
        var sp = services.BuildServiceProvider();
        var scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();

        var pending = new SimulatedPeerPendingInbox();
        var transportOptions = Options.Create(new TransportOptions { SimulatorPort = 5002 });
        var engine = new SignalProtocolEngine(new SystemClock());

        var sut = new SimulatorStateService(repo, diagnostics, pending, scopeFactory, transportOptions, engine);
        await ((ISimulatorStateInitializer)sut).InitializeAsync(CancellationToken.None);

        var sessionId = new SessionId(Guid.NewGuid());
        var badMessage = SessionRatchetMessage.FromBytes(RandomNumberGenerator.GetBytes(10));

        // Act
        var act = async () => await sut.DecryptSessionMessageAsync(peerId, sessionId, badMessage, CancellationToken.None);

        // Assert
        await act.Should().ThrowAsync<Exception>();
        diagnostics.Events.Should().Contain(e =>
            e.EventType == SimulatorDiagnosticEventType.DecryptFailure
            && e.PeerId == peerId
            && e.Message.Contains("Decrypt failure", StringComparison.Ordinal));
    }
}

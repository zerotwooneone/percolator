using System;
using System.Collections.Generic;
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

namespace Desktop.Wpf.Tests;

[TestFixture]
public sealed class SimulatedPeerRuntimeServiceDecryptFailureDiagnosticsTests
{
    private sealed class InMemoryRepository : ISimulatorStateRepository
    {
        public IReadOnlyList<SimulatedPeerModel> Peers { get; set; } = Array.Empty<SimulatedPeerModel>();

        public IReadOnlyList<PeerRelationship> Relationships { get; set; } = Array.Empty<PeerRelationship>();

        public SimulatedRelayModel? Relay { get; set; }

        public IReadOnlyList<PeerStateSnapshot> SavedPeers { get; private set; } = Array.Empty<PeerStateSnapshot>();

        public IReadOnlyList<PeerRelationshipSnapshot> SavedRelationships { get; private set; } = Array.Empty<PeerRelationshipSnapshot>();

        public RelayStateSnapshot? SavedRelay { get; private set; }

        public Task<IReadOnlyList<SimulatedPeerModel>> LoadPeersAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(Peers);

        public Task SavePeersAsync(
            IReadOnlyList<PeerStateSnapshot> peers,
            IReadOnlyList<PeerRelationshipSnapshot> relationships,
            CancellationToken cancellationToken = default)
        {
            SavedPeers = peers;
            SavedRelationships = relationships;
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<PeerRelationship>> LoadRelationshipsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(Relationships);

        public Task<SimulatedRelayModel?> LoadRelayAsync(Guid relayHostPeerId, CancellationToken cancellationToken = default)
            => Task.FromResult(Relay);

        public Task SaveRelayAsync(RelayStateSnapshot relay, CancellationToken cancellationToken = default)
        {
            SavedRelay = relay;
            return Task.CompletedTask;
        }
    }

    [Test]
    public async Task DecryptSessionMessageAsync_WhenDecryptThrows_EmitsDecryptFailureDiagnosticEvent()
    {
        // Arrange
        var peerId = Guid.NewGuid();
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
        var engine = new SignalProtocolEngine(new SystemClock());

        var sut = new SimulatorStateService(repo, diagnostics, pending, scopeFactory, engine);
        await ((ISimulatorStateInitializer)sut).InitializeAsync(CancellationToken.None);

        var sessionId = new SessionId(Guid.NewGuid());
        var badMessage = new SessionRatchetMessage(RandomNumberGenerator.GetBytes(10));

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

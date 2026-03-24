using System;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Desktop.Wpf.Features.Simulator;
using Desktop.Wpf.Features.Simulator.Protocol;
using Desktop.Wpf.Features.Sessions;
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
        public SimulatorStateDto? State { get; set; }

        public RelayPersistenceDto? Relay { get; set; }

        public Task<SimulatorStateDto?> LoadAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(State);

        public Task SaveAsync(SimulatorStateDto state, CancellationToken cancellationToken = default)
        {
            State = state;
            return Task.CompletedTask;
        }

        public Task<RelayPersistenceDto?> LoadRelayAsync(Guid relayHostPeerId, CancellationToken cancellationToken = default)
            => Task.FromResult(Relay);

        public Task SaveRelayAsync(RelayPersistenceDto relay, CancellationToken cancellationToken = default)
        {
            Relay = relay;
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
            State = new SimulatorStateDto
            {
                Version = 1,
                Peers =
                {
                    new SimulatedPeerDto
                    {
                        PeerId = peerId,
                        DisplayName = "peer",
                        IsOnline = true,
                        ReverseSignalKeys = new SimulatedPeerReverseSignalKeysDto
                        {
                            IdentitySigningKeySpki = identitySpki,
                            IdentitySigningKeyPrivateKeyEcPrivateKey = identityPriv
                        }
                    }
                }
            }
        };

        var services = new ServiceCollection();
        services.AddSingleton<IClock, SystemClock>();
        var sp = services.BuildServiceProvider();
        var scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();

        var keys = new SimulatedPeerKeyFactory();
        var options = Options.Create(new TransportOptions { GrpcPort = 5002 });
        var pending = new SimulatedPeerPendingInbox();
        var engine = new SignalProtocolEngine(new SystemClock());

        var sut = new SimulatorStateService(repo, keys, options, diagnostics, pending, scopeFactory, engine);
        await sut.InitializeAsync(CancellationToken.None);

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

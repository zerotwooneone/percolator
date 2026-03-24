using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Desktop.Wpf.Features.Simulator;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NUnit.Framework;
using Percolator.Application.Configuration;
using Percolator.Contracts;
using Percolator.Cryptography;
using Percolator.Cryptography.Primitives;
using Desktop.Wpf.Features.Simulator.Protocol;
using Desktop.Wpf.Features.Sessions;

namespace Desktop.Wpf.Tests;

[TestFixture]
public sealed class SimulatedPeerRuntimeFinalizeRelayedTests
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
    public async Task Relayed_invite_and_response_can_be_accepted_and_finalized()
    {
        var inviterPeerId = Guid.NewGuid();
        var acceptorPeerId = Guid.NewGuid();

        using var inviterIdentityEcdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var inviterIdentityPriv = inviterIdentityEcdh.ExportECPrivateKey();
        using var inviterIdentityEcdsa = ECDsa.Create(inviterIdentityEcdh.ExportParameters(true));
        var inviterIdentitySpki = inviterIdentityEcdsa.ExportSubjectPublicKeyInfo();

        using var acceptorIdentityEcdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var acceptorIdentityPriv = acceptorIdentityEcdh.ExportECPrivateKey();
        using var acceptorIdentityEcdsa = ECDsa.Create(acceptorIdentityEcdh.ExportParameters(true));
        var acceptorIdentitySpki = acceptorIdentityEcdsa.ExportSubjectPublicKeyInfo();

        using var inviterSignedPreKey = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var inviterSignedPreKeySpki = inviterSignedPreKey.PublicKey.ExportSubjectPublicKeyInfo();
        var inviterSignedPreKeyPriv = inviterSignedPreKey.ExportECPrivateKey();
        var preKeySig = inviterIdentityEcdsa.SignData(inviterSignedPreKeySpki, HashAlgorithmName.SHA256);

        var correlation = Guid.NewGuid();

        var pending = new SimulatedPeerPendingInbox();
        var repo = new InMemoryRepository
        {
            State = new SimulatorStateDto
            {
                Version = 1,
                Peers =
                {
                    new SimulatedPeerDto
                    {
                        PeerId = inviterPeerId,
                        DisplayName = "inviter",
                        IsOnline = true,
                        ReverseSignalKeys = new SimulatedPeerReverseSignalKeysDto
                        {
                            IdentitySigningKeySpki = inviterIdentitySpki,
                            IdentitySigningKeyPrivateKeyEcPrivateKey = inviterIdentityPriv
                        },
                        RuntimeStore = new SimulatedPeerRuntimeStoreDto
                        {
                            OutboundInvites = new List<SimulatedOutboundInviteDto>
                            {
                                new SimulatedOutboundInviteDto
                                {
                                    CorrelationId = correlation,
                                    SignedPreKeyPrivateEcPrivateKey = inviterSignedPreKeyPriv
                                }
                            }
                        }
                    },
                    new SimulatedPeerDto
                    {
                        PeerId = acceptorPeerId,
                        DisplayName = "acceptor",
                        IsOnline = true,
                        ReverseSignalKeys = new SimulatedPeerReverseSignalKeysDto
                        {
                            IdentitySigningKeySpki = acceptorIdentitySpki,
                            IdentitySigningKeyPrivateKeyEcPrivateKey = acceptorIdentityPriv
                        }
                    }
                }
            }
        };

        var services = new ServiceCollection();
        services.AddSingleton<IClock, SystemClock>();
        var sp = services.BuildServiceProvider();
        var scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();

        var engine = new SignalProtocolEngine(new SystemClock());
        var keys = new SimulatedPeerKeyFactory();
        var options = Options.Create(new TransportOptions { GrpcPort = 5002 });
        var diagnostics = new SimulatorDiagnosticsService();

        var state = new SimulatorStateService(
            store: repo,
            keys: keys,
            transportOptions: options,
            diagnostics: diagnostics,
            pending: pending,
            scopeFactory: scopeFactory,
            engine: engine);

        await state.InitializeAsync(CancellationToken.None);

        var payload = new InviteHandshakeRequestPayload
        {
            Version = 1,
            InviterHost = "127.77.1.1",
            InviterPort = 5002,
            ExpiresAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow.AddMinutes(10)),
            RequestCorrelationId = correlation.ToString(),
            InviterPreKey = new InviteHandshakePreKeyBundle
            {
                Version = 1,
                InviterSignedPreKey = ByteString.CopyFrom(inviterSignedPreKeySpki),
                PreKeySignature = ByteString.CopyFrom(preKeySig)
            }
        };

        var payloadBytes = payload.ToByteArray();
        var payloadSig = inviterIdentityEcdsa.SignData(payloadBytes, HashAlgorithmName.SHA256);

        var invite = new EstablishDirectSessionRequest
        {
            Version = 1,
            InviterIdentityKey = ByteString.CopyFrom(inviterIdentitySpki),
            Payload = ByteString.CopyFrom(payloadBytes),
            PayloadSignature = ByteString.CopyFrom(payloadSig)
        };

        var acceptance = await state.AcceptReverseSignalInviteAsync(
            simulatedPeerId: acceptorPeerId,
            inviterPeerId: inviterPeerId,
            invite: invite,
            cancellationToken: CancellationToken.None);

        await state.ReceiveInviteHandshakeResponseFromMainAsync(inviterPeerId, acceptance.Response, CancellationToken.None);

        var finalizedSid = await state.TryFinalizeInviteHandshakeResponseFromMainAsync(
            simulatedPeerId: inviterPeerId,
            acceptorPeerId: acceptorPeerId,
            requestCorrelationId: correlation,
            cancellationToken: CancellationToken.None);

        finalizedSid.Should().NotBeNull();
        pending.TryGetInviteHandshakeResponse(inviterPeerId, correlation, out _).Should().BeFalse();
    }
}

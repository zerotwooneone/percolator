using System;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Desktop.Wpf.Features.Simulator;
using Desktop.Wpf.Features.Simulator.Protocol;
using Desktop.Wpf.Features.Sessions;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using NUnit.Framework;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Percolator.Application.Configuration;
using Percolator.Contracts;
using Percolator.Cryptography;

namespace Desktop.Wpf.Tests;

[TestFixture]
public sealed class SimulatedPeerRuntimeFinalizeTests
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
    public async Task Accept_finalize_creates_session_when_signed_prekey_private_is_recorded()
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

        var correlation = Guid.NewGuid();

        using var inviterSignedPreKey = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var inviterSignedPreKeySpki = inviterSignedPreKey.PublicKey.ExportSubjectPublicKeyInfo();
        var inviterSignedPreKeyPriv = inviterSignedPreKey.ExportECPrivateKey();
        var preKeySig = inviterIdentityEcdsa.SignData(inviterSignedPreKeySpki, HashAlgorithmName.SHA256);

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
                            OutboundInvites =
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

        var pending = new SimulatedPeerPendingInbox();
        var diagnostics = new SimulatorDiagnosticsService();
        var keys = new SimulatedPeerKeyFactory();
        var options = Options.Create(new TransportOptions { GrpcPort = 5002 });
        var engine = new SignalProtocolEngine(new SystemClock());

        var sut = new SimulatorStateService(repo, keys, options, diagnostics, pending, scopeFactory, engine);
        await sut.InitializeAsync(CancellationToken.None);

        var payload = new InviteHandshakeRequestPayload
        {
            Version = 1,
            InviterHost = "127.0.0.1",
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

        var acceptance = await sut.AcceptReverseSignalInviteAsync(
            simulatedPeerId: acceptorPeerId,
            inviterPeerId: inviterPeerId,
            invite: invite,
            cancellationToken: CancellationToken.None);

        await sut.ReceiveInviteHandshakeResponseFromMainAsync(inviterPeerId, acceptance.Response, CancellationToken.None);

        var finalizedSid = await sut.TryFinalizeInviteHandshakeResponseFromMainAsync(
            simulatedPeerId: inviterPeerId,
            acceptorPeerId: acceptorPeerId,
            requestCorrelationId: correlation,
            cancellationToken: CancellationToken.None);

        finalizedSid.Should().NotBeNull();

        pending.TryGetInviteHandshakeResponse(inviterPeerId, correlation, out _).Should().BeFalse();
    }

    [Test]
    public async Task Accept_finalize_returns_null_when_missing_signed_prekey_private()
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

        var correlation = Guid.NewGuid();

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

        var pending = new SimulatedPeerPendingInbox();
        var diagnostics = new SimulatorDiagnosticsService();
        var keys = new SimulatedPeerKeyFactory();
        var options = Options.Create(new TransportOptions { GrpcPort = 5002 });
        var engine = new SignalProtocolEngine(new SystemClock());

        var sut = new SimulatorStateService(repo, keys, options, diagnostics, pending, scopeFactory, engine);
        await sut.InitializeAsync(CancellationToken.None);

        await sut.ReceiveInviteHandshakeResponseFromMainAsync(inviterPeerId, new InviteHandshakeResponse
        {
            Version = 1,
            RequestCorrelationId = correlation.ToString(),
            AcceptorIdentityKey = ByteString.CopyFrom(acceptorIdentitySpki),
            AcceptorX3DhEphemeralKey = ByteString.CopyFrom(new byte[] { 0x01 }),
            InitialRatchetMessage = ByteString.CopyFrom(new byte[] { 0x02 })
        }, CancellationToken.None);

        var finalizedSid = await sut.TryFinalizeInviteHandshakeResponseFromMainAsync(
            simulatedPeerId: inviterPeerId,
            acceptorPeerId: acceptorPeerId,
            requestCorrelationId: correlation,
            cancellationToken: CancellationToken.None);

        finalizedSid.Should().BeNull();
        pending.TryGetInviteHandshakeResponse(inviterPeerId, correlation, out _).Should().BeTrue();
    }
}

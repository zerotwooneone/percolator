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
using Desktop.Wpf.Features.Simulator.Models;

namespace Desktop.Wpf.Tests;

[TestFixture]
public sealed class SimulatedPeerRuntimeFinalizeRelayedTests
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
            Peers = new[]
            {
                new SimulatedPeerModel(
                    peerId: inviterPeerId,
                    displayName: "inviter",
                    isOnline: true,
                    isRelayCapable: false,
                    identitySigningKeySpki: inviterIdentitySpki,
                    identitySigningKeyPrivateKeyEcPrivateKey: inviterIdentityPriv),
                new SimulatedPeerModel(
                    peerId: acceptorPeerId,
                    displayName: "acceptor",
                    isOnline: true,
                    isRelayCapable: false,
                    identitySigningKeySpki: acceptorIdentitySpki,
                    identitySigningKeyPrivateKeyEcPrivateKey: acceptorIdentityPriv)
            }
        };

        repo.Peers[0].OutboundInvitesMutable.Add(new SimulatedOutboundInviteModel(correlation, inviterSignedPreKeyPriv));

        var services = new ServiceCollection();
        services.AddSingleton<IClock, SystemClock>();
        var sp = services.BuildServiceProvider();
        var scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();

        var engine = new SignalProtocolEngine(new SystemClock());
        var diagnostics = new SimulatorDiagnosticsService();

        var state = new SimulatorStateService(
            store: repo,
            diagnostics: diagnostics,
            pending: pending,
            scopeFactory: scopeFactory,
            engine: engine);

        await ((ISimulatorStateInitializer)state).InitializeAsync(CancellationToken.None);

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

        repo.Peers.Single(p => p.PeerId == inviterPeerId).Sessions.Count.Should().Be(1);
        pending.TryGetInviteHandshakeResponse(inviterPeerId, correlation, out _).Should().BeFalse();
    }
}

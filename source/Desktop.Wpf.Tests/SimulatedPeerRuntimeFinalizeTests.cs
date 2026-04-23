using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Desktop.Wpf.Features.Simulator;
using Desktop.Wpf.Features.Simulator.Protocol;
using Desktop.Wpf.Features.Simulator.Models;
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
using Percolator.Network;

namespace Desktop.Wpf.Tests;

[TestFixture]
public sealed class SimulatedPeerRuntimeFinalizeTests
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
    public async Task Accept_finalize_creates_session_when_signed_prekey_private_is_recorded()
    {
        var inviterPeerId = new PeerId(Guid.NewGuid());
        var acceptorPeerId = new PeerId(Guid.NewGuid());

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
            Peers = new[]
            {
                new SimulatedPeerModel(
                    peerId: inviterPeerId,
                    selfIdentityId: 99000,
                    displayName: "inviter",
                    isOnline: true,
                    isRelayCapable: false,
                    identitySigningKeySpki: inviterIdentitySpki,
                    identitySigningKeyPrivateKeyEcPrivateKey: inviterIdentityPriv),
                new SimulatedPeerModel(
                    peerId: acceptorPeerId,
                    selfIdentityId: 99001,
                    displayName: "acceptor",
                    isOnline: true,
                    isRelayCapable: false,
                    identitySigningKeySpki: acceptorIdentitySpki,
                    identitySigningKeyPrivateKeyEcPrivateKey: acceptorIdentityPriv)
            }
        };

        var services = new ServiceCollection();
        services.AddSingleton<IClock, SystemClock>();
        var sp = services.BuildServiceProvider();
        var scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();

        var pending = new SimulatedPeerPendingInbox();
        var diagnostics = new SimulatorDiagnosticsService();
        var transportOptions = Options.Create(new TransportOptions { SimulatorPort = 5002 });
        var engine = new SignalProtocolEngine(new SystemClock());

        var sut = new SimulatorStateService(repo, diagnostics, pending, scopeFactory, transportOptions, engine);
        await ((ISimulatorStateInitializer)sut).InitializeAsync(CancellationToken.None);

        sut.Peers.Single(p => p.PeerId == inviterPeerId)
            .OutboundInvitesMutable
            .Add(new SimulatedOutboundInviteModel(correlation, inviterSignedPreKeyPriv));

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

        sut.Peers.Single(p => p.PeerId == inviterPeerId).Sessions.Count.Should().Be(1);

        pending.TryGetInviteHandshakeResponse(inviterPeerId, correlation, out _).Should().BeFalse();
    }

    [Test]
    public async Task Accept_finalize_returns_null_when_missing_signed_prekey_private()
    {
        var inviterPeerId = new PeerId(Guid.NewGuid());
        var acceptorPeerId = new PeerId(Guid.NewGuid());

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
            Peers = new[]
            {
                new SimulatedPeerModel(
                    peerId: inviterPeerId,
                    selfIdentityId: 99000,
                    displayName: "inviter",
                    isOnline: true,
                    isRelayCapable: false,
                    identitySigningKeySpki: inviterIdentitySpki,
                    identitySigningKeyPrivateKeyEcPrivateKey: inviterIdentityPriv),
                new SimulatedPeerModel(
                    peerId: acceptorPeerId,
                    selfIdentityId: 99001,
                    displayName: "acceptor",
                    isOnline: true,
                    isRelayCapable: false,
                    identitySigningKeySpki: acceptorIdentitySpki,
                    identitySigningKeyPrivateKeyEcPrivateKey: acceptorIdentityPriv)
            }
        };

        var services = new ServiceCollection();
        services.AddSingleton<IClock, SystemClock>();
        var sp = services.BuildServiceProvider();
        var scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();

        var pending = new SimulatedPeerPendingInbox();
        var diagnostics = new SimulatorDiagnosticsService();
        var transportOptions = Options.Create(new TransportOptions { SimulatorPort = 5002 });
        var engine = new SignalProtocolEngine(new SystemClock());

        var sut = new SimulatorStateService(repo, diagnostics, pending, scopeFactory, transportOptions, engine);
        await ((ISimulatorStateInitializer)sut).InitializeAsync(CancellationToken.None);

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

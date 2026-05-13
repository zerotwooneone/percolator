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
    [Test]
    public async Task Accept_finalize_creates_session_when_signed_prekey_private_is_recorded()
    {
        // Arrange
        var inviterPeerId = new PeerId(Guid.NewGuid());
        var acceptorPeerId = new PeerId(Guid.NewGuid());
        var correlation = Guid.NewGuid();

        var inviterPeer = CryptoTestHelpers.CreateTestPeer(
            inviterPeerId, 99000, "inviter", false,
            new System.Net.DnsEndPoint("127.77.1.1", 5002));
        var acceptorPeer = CryptoTestHelpers.CreateTestPeer(
            acceptorPeerId, 99001, "acceptor", false,
            new System.Net.DnsEndPoint("127.77.1.2", 5002));

        var (spkId, spkPriv, spkSpki, spkSig) = CryptoTestHelpers.CreateSignedPreKey(
            inviterPeer.IdentitySigningKeySpki,
            inviterPeer.IdentitySigningKeyPrivateKeyEcPrivateKey);

        var repo = new InMemorySimulatorStateRepository();
        repo.Seed(new SimulatorStateSnapshot(
            Version: 1,
            Peers: new[] { inviterPeer.Freeze(), acceptorPeer.Freeze() },
            Relationships: Array.Empty<PeerRelationshipSnapshot>(),
            Relays: Array.Empty<RelayStateSnapshot>(),
            Groups: Array.Empty<GroupConversationDto>()));

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

        // Setup: Add outbound invite to simulate pre-existing invite state
        sut.Peers.Single(p => p.PeerId == inviterPeerId)
            .OutboundInvitesMutable
            .Add(new SimulatedOutboundInviteModel(correlation, spkPriv));

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
                InviterSignedPreKey = ByteString.CopyFrom(spkSpki),
                PreKeySignature = ByteString.CopyFrom(spkSig)
            }
        };

        var payloadBytes = payload.ToByteArray();
        var payloadSig = CryptoTestHelpers.SignPayload(
            payloadBytes,
            inviterPeer.IdentitySigningKeySpki,
            inviterPeer.IdentitySigningKeyPrivateKeyEcPrivateKey);

        var invite = new EstablishDirectSessionRequest
        {
            Version = 1,
            InviterIdentityKey = ByteString.CopyFrom(inviterPeer.IdentitySigningKeySpki),
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

        // Assert: Session was created and pending response was consumed
        finalizedSid.Should().NotBeNull();
        sut.Peers.Single(p => p.PeerId == inviterPeerId).Sessions.Count.Should().Be(1);
        pending.TryGetInviteHandshakeResponse(inviterPeerId, correlation, out _).Should().BeFalse();
    }

    [Test]
    public async Task Accept_finalize_returns_null_when_missing_signed_prekey_private()
    {
        // Arrange
        var inviterPeerId = new PeerId(Guid.NewGuid());
        var acceptorPeerId = new PeerId(Guid.NewGuid());
        var correlation = Guid.NewGuid();

        var inviterPeer = CryptoTestHelpers.CreateTestPeer(
            inviterPeerId, 99000, "inviter", false,
            new System.Net.DnsEndPoint("127.77.1.1", 5002));
        var acceptorPeer = CryptoTestHelpers.CreateTestPeer(
            acceptorPeerId, 99001, "acceptor", false,
            new System.Net.DnsEndPoint("127.77.1.2", 5002));

        var repo = new InMemorySimulatorStateRepository();
        repo.Seed(new SimulatorStateSnapshot(
            Version: 1,
            Peers: new[] { inviterPeer.Freeze(), acceptorPeer.Freeze() },
            Relationships: Array.Empty<PeerRelationshipSnapshot>(),
            Relays: Array.Empty<RelayStateSnapshot>(),
            Groups: Array.Empty<GroupConversationDto>()));

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
            AcceptorIdentityKey = ByteString.CopyFrom(acceptorPeer.IdentitySigningKeySpki),
            AcceptorX3DhEphemeralKey = ByteString.CopyFrom(new byte[] { 0x01 }),
            InitialRatchetMessage = ByteString.CopyFrom(new byte[] { 0x02 })
        }, CancellationToken.None);

        var finalizedSid = await sut.TryFinalizeInviteHandshakeResponseFromMainAsync(
            simulatedPeerId: inviterPeerId,
            acceptorPeerId: acceptorPeerId,
            requestCorrelationId: correlation,
            cancellationToken: CancellationToken.None);

        // Assert: Finalization returns null when signed prekey private is missing
        finalizedSid.Should().BeNull();
        pending.TryGetInviteHandshakeResponse(inviterPeerId, correlation, out _).Should().BeTrue();
    }
}

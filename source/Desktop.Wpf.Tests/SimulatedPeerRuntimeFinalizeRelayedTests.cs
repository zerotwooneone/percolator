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
using Percolator.Network;
using Desktop.Wpf.Features.Simulator.Protocol;
using Desktop.Wpf.Features.Sessions;
using Desktop.Wpf.Features.Simulator.Models;

namespace Desktop.Wpf.Tests;

[TestFixture]
public sealed class SimulatedPeerRuntimeFinalizeRelayedTests
{
    [Test]
    public async Task Relayed_invite_and_response_can_be_accepted_and_finalized()
    {
        // Arrange
        var inviterPeerId = new Percolator.Network.PeerId(Guid.NewGuid());
        var acceptorPeerId = new Percolator.Network.PeerId(Guid.NewGuid());
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

        var pending = new SimulatedPeerPendingInbox();
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

        var transportOptions = Options.Create(new TransportOptions { SimulatorPort = 5002 });
        var engine = new SignalProtocolEngine(new SystemClock());
        var diagnostics = new SimulatorDiagnosticsService();

        var state = new SimulatorStateService(
            store: repo,
            diagnostics: diagnostics,
            pending: pending,
            scopeFactory: scopeFactory,
            transportOptions: transportOptions,
            engine: engine);

        await ((ISimulatorStateInitializer)state).InitializeAsync(CancellationToken.None);

        // Setup: Add outbound invite to simulate pre-existing invite state
        state.Peers.Single(p => p.PeerId == inviterPeerId)
            .OutboundInvitesMutable
            .Add(new SimulatedOutboundInviteModel(correlation, spkPriv));

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

        // Assert: Session was created and pending response was consumed
        finalizedSid.Should().NotBeNull();
        state.Peers.Single(p => p.PeerId == inviterPeerId).Sessions.Count.Should().Be(1);
        pending.TryGetInviteHandshakeResponse(inviterPeerId, correlation, out _).Should().BeFalse();
    }
}

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Desktop.Wpf.Features.Simulator;
using Desktop.Wpf.Features.Simulator.Protocol;
using Desktop.Wpf.Features.Simulator.Models;
using Desktop.Wpf.Features.Sessions;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NUnit.Framework;
using Percolator.Application.Configuration;
using Percolator.Contracts;
using Percolator.Cryptography;
using Percolator.Network;

namespace Desktop.Wpf.Tests;

[TestFixture]
public sealed class SimulatedPeerRuntimeFinalizeTests
{
    [Test]
    public async Task SimulatorInitiatedHandshake_CreatesSession_WhenMainAccepts()
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

        var diagnostics = new SimulatorDiagnosticsService();
        var fakeTimeProvider = new FakeTimeProvider();
        var transportOptions = Options.Create(new TransportOptions { SimulatorPort = 5002 });
        var engine = new SignalProtocolEngine(new SystemClock());

        var sut = new SimulatorStateService(repo, diagnostics, scopeFactory, new NoopSimulatorToMainTransportService(), transportOptions, engine, fakeTimeProvider);
        await ((ISimulatorStateInitializer)sut).InitializeAsync(CancellationToken.None);

        // Setup: Add outbound invite to simulate simulator-initiated handshake state
        // Note: This is internal state manipulation, which is necessary to test the service layer
        // in isolation. In production, outbound invites are created by ViewModels via UI commands.
        sut.Peers.Single(p => p.PeerId == inviterPeerId)
            .OutboundInvitesMutable
            .Add(new SimulatedOutboundInviteModel(correlation, spkPriv));

        // Act: Generate a valid response using the crypto engine, then handle it
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

        // Use AcceptInboundDirectInviteAsync to generate a valid response via crypto engine
        // This simulates Main accepting the invite and generating the response
        var acceptance = await sut.AcceptInboundDirectInviteAsync(
            simulatedPeerId: acceptorPeerId,
            inviterPeerId: inviterPeerId,
            invite: invite,
            cancellationToken: CancellationToken.None);

        // Chunk A: Response is now finalized immediately on receipt
        await sut.HandleInboundInviteHandshakeResponseFromMainAsync(inviterPeerId, acceptance.Response, CancellationToken.None);

        // Assert: Session was created immediately (Chunk A behavior)
        sut.Peers.Single(p => p.PeerId == inviterPeerId).Sessions.Count.Should().Be(1);
        // Outbound invite should be removed
        sut.Peers.Single(p => p.PeerId == inviterPeerId).OutboundInvitesMutable.Count.Should().Be(0);
        // UI state should be Established
        sut.Peers.Single(p => p.PeerId == inviterPeerId).UiState.CurrentValue.Should().Be(SimulatorPeerUiState.Established);
    }

    [Test]
    public async Task HandleInboundInviteHandshakeResponse_does_not_create_session_when_no_matching_outbound_invite()
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

        var diagnostics = new SimulatorDiagnosticsService();
        var fakeTimeProvider = new FakeTimeProvider();
        var transportOptions = Options.Create(new TransportOptions { SimulatorPort = 5002 });
        var engine = new SignalProtocolEngine(new SystemClock());

        var sut = new SimulatorStateService(repo, diagnostics, scopeFactory, new NoopSimulatorToMainTransportService(), transportOptions, engine, fakeTimeProvider);
        await ((ISimulatorStateInitializer)sut).InitializeAsync(CancellationToken.None);

        // Act: Response arrives but there's no matching outbound invite
        await sut.HandleInboundInviteHandshakeResponseFromMainAsync(inviterPeerId, new InviteHandshakeResponse
        {
            Version = 1,
            RequestCorrelationId = correlation.ToString(),
            AcceptorIdentityKey = ByteString.CopyFrom(acceptorPeer.IdentitySigningKeySpki),
            AcceptorX3DhEphemeralKey = ByteString.CopyFrom(new byte[] { 0x01 }),
            InitialRatchetMessage = ByteString.CopyFrom(new byte[] { 0x02 })
        }, CancellationToken.None);

        // Assert: Session was NOT created because there was no matching outbound invite
        sut.Peers.Single(p => p.PeerId == inviterPeerId).Sessions.Count.Should().Be(0);
        // Outbound invites should still be empty (we never added one)
        sut.Peers.Single(p => p.PeerId == inviterPeerId).OutboundInvitesMutable.Count.Should().Be(0);
    }

    [Test]
    public async Task HandleInboundInviteHandshakeResponse_throws_when_missing_acceptor_identity_key()
    {
        // Arrange
        var inviterPeerId = new PeerId(Guid.NewGuid());
        var correlation = Guid.NewGuid();

        var inviterPeer = CryptoTestHelpers.CreateTestPeer(
            inviterPeerId, 99000, "inviter", false,
            new System.Net.DnsEndPoint("127.77.1.1", 5002));

        var repo = new InMemorySimulatorStateRepository();
        repo.Seed(new SimulatorStateSnapshot(
            Version: 1,
            Peers: new[] { inviterPeer.Freeze() },
            Relationships: Array.Empty<PeerRelationshipSnapshot>(),
            Relays: Array.Empty<RelayStateSnapshot>(),
            Groups: Array.Empty<GroupConversationDto>()));

        var services = new ServiceCollection();
        services.AddSingleton<IClock, SystemClock>();
        var sp = services.BuildServiceProvider();
        var scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();

        var diagnostics = new SimulatorDiagnosticsService();
        var fakeTimeProvider = new FakeTimeProvider();
        var transportOptions = Options.Create(new TransportOptions { SimulatorPort = 5002 });
        var engine = new SignalProtocolEngine(new SystemClock());

        var sut = new SimulatorStateService(repo, diagnostics, scopeFactory, new NoopSimulatorToMainTransportService(), transportOptions, engine, fakeTimeProvider);
        await ((ISimulatorStateInitializer)sut).InitializeAsync(CancellationToken.None);

        // Act & Assert: Response with missing acceptor_identity_key should throw
        await FluentActions.Awaiting(() => sut.HandleInboundInviteHandshakeResponseFromMainAsync(inviterPeerId, new InviteHandshakeResponse
        {
            Version = 1,
            RequestCorrelationId = correlation.ToString(),
            // AcceptorIdentityKey is missing
            AcceptorX3DhEphemeralKey = ByteString.CopyFrom(new byte[] { 0x01 }),
            InitialRatchetMessage = ByteString.CopyFrom(new byte[] { 0x02 })
        }, CancellationToken.None)).Should().ThrowAsync<InvalidOperationException>();
    }

    [Test]
    public async Task HandleInboundInviteHandshakeResponse_throws_when_missing_acceptor_x3dh_ephemeral_key()
    {
        // Arrange
        var inviterPeerId = new PeerId(Guid.NewGuid());
        var correlation = Guid.NewGuid();

        var inviterPeer = CryptoTestHelpers.CreateTestPeer(
            inviterPeerId, 99000, "inviter", false,
            new System.Net.DnsEndPoint("127.77.1.1", 5002));

        var repo = new InMemorySimulatorStateRepository();
        repo.Seed(new SimulatorStateSnapshot(
            Version: 1,
            Peers: new[] { inviterPeer.Freeze() },
            Relationships: Array.Empty<PeerRelationshipSnapshot>(),
            Relays: Array.Empty<RelayStateSnapshot>(),
            Groups: Array.Empty<GroupConversationDto>()));

        var services = new ServiceCollection();
        services.AddSingleton<IClock, SystemClock>();
        var sp = services.BuildServiceProvider();
        var scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();

        var diagnostics = new SimulatorDiagnosticsService();
        var fakeTimeProvider = new FakeTimeProvider();
        var transportOptions = Options.Create(new TransportOptions { SimulatorPort = 5002 });
        var engine = new SignalProtocolEngine(new SystemClock());

        var sut = new SimulatorStateService(repo, diagnostics, scopeFactory, new NoopSimulatorToMainTransportService(), transportOptions, engine, fakeTimeProvider);
        await ((ISimulatorStateInitializer)sut).InitializeAsync(CancellationToken.None);

        // Act & Assert: Response with missing acceptor_x3dh_ephemeral_key should throw
        await FluentActions.Awaiting(() => sut.HandleInboundInviteHandshakeResponseFromMainAsync(inviterPeerId, new InviteHandshakeResponse
        {
            Version = 1,
            RequestCorrelationId = correlation.ToString(),
            AcceptorIdentityKey = ByteString.CopyFrom(new byte[] { 0x01 }),
            // AcceptorX3DhEphemeralKey is missing
            InitialRatchetMessage = ByteString.CopyFrom(new byte[] { 0x02 })
        }, CancellationToken.None)).Should().ThrowAsync<InvalidOperationException>();
    }

    [Test]
    public async Task HandleInboundInviteHandshakeResponse_throws_when_missing_initial_ratchet_message()
    {
        // Arrange
        var inviterPeerId = new PeerId(Guid.NewGuid());
        var correlation = Guid.NewGuid();

        var inviterPeer = CryptoTestHelpers.CreateTestPeer(
            inviterPeerId, 99000, "inviter", false,
            new System.Net.DnsEndPoint("127.77.1.1", 5002));

        var repo = new InMemorySimulatorStateRepository();
        repo.Seed(new SimulatorStateSnapshot(
            Version: 1,
            Peers: new[] { inviterPeer.Freeze() },
            Relationships: Array.Empty<PeerRelationshipSnapshot>(),
            Relays: Array.Empty<RelayStateSnapshot>(),
            Groups: Array.Empty<GroupConversationDto>()));

        var services = new ServiceCollection();
        services.AddSingleton<IClock, SystemClock>();
        var sp = services.BuildServiceProvider();
        var scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();

        var diagnostics = new SimulatorDiagnosticsService();
        var fakeTimeProvider = new FakeTimeProvider();
        var transportOptions = Options.Create(new TransportOptions { SimulatorPort = 5002 });
        var engine = new SignalProtocolEngine(new SystemClock());

        var sut = new SimulatorStateService(repo, diagnostics, scopeFactory, new NoopSimulatorToMainTransportService(), transportOptions, engine, fakeTimeProvider);
        await ((ISimulatorStateInitializer)sut).InitializeAsync(CancellationToken.None);

        // Act & Assert: Response with missing initial_ratchet_message should throw
        await FluentActions.Awaiting(() => sut.HandleInboundInviteHandshakeResponseFromMainAsync(inviterPeerId, new InviteHandshakeResponse
        {
            Version = 1,
            RequestCorrelationId = correlation.ToString(),
            AcceptorIdentityKey = ByteString.CopyFrom(new byte[] { 0x01 }),
            AcceptorX3DhEphemeralKey = ByteString.CopyFrom(new byte[] { 0x02 })
            // InitialRatchetMessage is missing
        }, CancellationToken.None)).Should().ThrowAsync<InvalidOperationException>();
    }
}

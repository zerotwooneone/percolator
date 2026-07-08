using System;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Desktop.Wpf.Features.Sessions;
using Desktop.Wpf.Features.Simulator;
using Desktop.Wpf.Features.Simulator.Models;
using Desktop.Wpf.Features.Simulator.Protocol;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Time.Testing;
using NUnit.Framework;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Percolator.Application.Configuration;
using Percolator.Contracts;
using Percolator.Cryptography;
using Percolator.Network;

namespace Desktop.Wpf.Tests;

[TestFixture]
public sealed class SimulatedPeerPendingInboundDirectInviteTests
{
    [Test]
    public async Task ReceiveEstablishDirectSessionFromMainAsync_QueuesPendingInvite_DoesNotCreateSession()
    {
        // Arrange
        var simulatedPeerId = new NetworkPeerId(1);
        var mainPeerId = new NetworkPeerId(2);
        var correlationId = Guid.NewGuid();

        var simulatedPeer = CryptoTestHelpers.CreateTestPeer(
            simulatedPeerId, 99000, "sim", false,
            new System.Net.DnsEndPoint("127.77.1.1", 5002));

        var repo = new InMemorySimulatorStateRepository();
        repo.Seed(new SimulatorStateSnapshot(
            Version: 1,
            Peers: new[] { simulatedPeer.Freeze() },
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

        // Create a valid EstablishDirectSessionRequest
        using var mainIdentityEcdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var mainIdentityPriv = mainIdentityEcdh.ExportECPrivateKey();
        using var mainIdentityEcdsa = ECDsa.Create(mainIdentityEcdh.ExportParameters(true));
        var mainIdentitySpki = mainIdentityEcdsa.ExportSubjectPublicKeyInfo();

        using var mainSignedPreKey = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var mainSignedPreKeySpki = mainSignedPreKey.PublicKey.ExportSubjectPublicKeyInfo();
        var mainSignedPreKeyPriv = mainSignedPreKey.ExportECPrivateKey();

        var mainPayloadSig = mainIdentityEcdsa.SignData(mainSignedPreKeySpki, HashAlgorithmName.SHA256);

        var payload = new InviteHandshakeRequestPayload
        {
            Version = 1,
            InviterHost = "127.0.0.1",
            InviterPort = 5002,
            ExpiresAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow.AddMinutes(10)),
            RequestCorrelationId = correlationId.ToString(),
            InviterPreKey = new InviteHandshakePreKeyBundle
            {
                Version = 1,
                InviterSignedPreKey = ByteString.CopyFrom(mainSignedPreKeySpki),
                PreKeySignature = ByteString.CopyFrom(mainPayloadSig)
            }
        };

        var payloadBytes = payload.ToByteArray();
        var payloadSig = mainIdentityEcdsa.SignData(payloadBytes, HashAlgorithmName.SHA256);

        var request = new EstablishDirectSessionRequest
        {
            Version = 1,
            InviterIdentityKey = ByteString.CopyFrom(mainIdentitySpki),
            Payload = ByteString.CopyFrom(payloadBytes),
            PayloadSignature = ByteString.CopyFrom(payloadSig)
        };

        // Act
        var response = await sut.ReceiveEstablishDirectSessionFromMainAsync(
            simulatedPeerId,
            mainPeerId,
            request,
            CancellationToken.None);

        // Assert
        response.Should().NotBeNull();
        response.Version.Should().Be(1);
        response.Queued.Should().NotBeNull();
        response.Queued.Version.Should().Be(1);
        response.Queued.RequestCorrelationId.Should().Be(correlationId.ToString());

        // No session should be created
        var peer = sut.Peers.Single(p => p.NetworkPeerId == simulatedPeerId);
        peer.Sessions.Count.Should().Be(0);

        // Pending invite should be persisted
        peer.PendingInboundDirectInvites.Count.Should().Be(1);
        var pendingInvite = peer.PendingInboundDirectInvites.Single();
        pendingInvite.CorrelationId.Should().Be(correlationId);
        pendingInvite.InviterNetworkPeerId.Should().Be(mainPeerId);
    }

    [Test]
    public async Task AcceptPendingInboundDirectInviteAsync_CreatesSession_WhenInvitedPeerAccepts()
    {
        // Arrange
        var simulatedPeerId = new NetworkPeerId(3);
        var mainPeerId = new NetworkPeerId(4);
        var correlationId = Guid.NewGuid();

        var simulatedPeer = CryptoTestHelpers.CreateTestPeer(
            simulatedPeerId, 99000, "sim", false,
            new System.Net.DnsEndPoint("127.77.1.1", 5002));

        var repo = new InMemorySimulatorStateRepository();
        repo.Seed(new SimulatorStateSnapshot(
            Version: 1,
            Peers: new[] { simulatedPeer.Freeze() },
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

        // Create and add a pending inbound direct invite
        using var mainIdentityEcdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var mainIdentityPriv = mainIdentityEcdh.ExportECPrivateKey();
        using var mainIdentityEcdsa = ECDsa.Create(mainIdentityEcdh.ExportParameters(true));
        var mainIdentitySpki = mainIdentityEcdsa.ExportSubjectPublicKeyInfo();

        using var mainSignedPreKey = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var mainSignedPreKeySpki = mainSignedPreKey.PublicKey.ExportSubjectPublicKeyInfo();
        var mainSignedPreKeyPriv = mainSignedPreKey.ExportECPrivateKey();

        var mainPayloadSig = mainIdentityEcdsa.SignData(mainSignedPreKeySpki, HashAlgorithmName.SHA256);

        var payload = new InviteHandshakeRequestPayload
        {
            Version = 1,
            InviterHost = "127.0.0.1",
            InviterPort = 5002,
            ExpiresAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow.AddMinutes(10)),
            RequestCorrelationId = correlationId.ToString(),
            InviterPreKey = new InviteHandshakePreKeyBundle
            {
                Version = 1,
                InviterSignedPreKey = ByteString.CopyFrom(mainSignedPreKeySpki),
                PreKeySignature = ByteString.CopyFrom(mainPayloadSig)
            }
        };

        var payloadBytes = payload.ToByteArray();
        var payloadSig = mainIdentityEcdsa.SignData(payloadBytes, HashAlgorithmName.SHA256);

        var request = new EstablishDirectSessionRequest
        {
            Version = 1,
            InviterIdentityKey = ByteString.CopyFrom(mainIdentitySpki),
            Payload = ByteString.CopyFrom(payloadBytes),
            PayloadSignature = ByteString.CopyFrom(payloadSig)
        };

        // Manually add pending invite to simulate the queued state
        var peer = sut.Peers.Single(p => p.NetworkPeerId == simulatedPeerId);
        peer.AddPendingInboundDirectInvite(correlationId, request.ToByteArray(), mainIdentitySpki, mainPeerId);

        // Act
        await sut.AcceptPendingInboundDirectInviteAsync(
            simulatedPeerId,
            correlationId,
            CancellationToken.None);

        // Assert
        // Session should be created
        peer.Sessions.Count.Should().Be(1);
        var session = peer.Sessions.First().Value;
        session.RemotePeerId.Value.Should().Be(mainPeerId.Value);

        // Pending invite should be removed
        peer.PendingInboundDirectInvites.Count.Should().Be(0);
    }

    [Test]
    public async Task RejectPendingInboundDirectInviteAsync_DoesNotCreateSession_ClearsPending()
    {
        // Arrange
        var simulatedPeerId = new NetworkPeerId(5);
        var mainPeerId = new NetworkPeerId(6);
        var correlationId = Guid.NewGuid();

        var simulatedPeer = CryptoTestHelpers.CreateTestPeer(
            simulatedPeerId, 99000, "sim", false,
            new System.Net.DnsEndPoint("127.77.1.1", 5002));

        var repo = new InMemorySimulatorStateRepository();
        repo.Seed(new SimulatorStateSnapshot(
            Version: 1,
            Peers: new[] { simulatedPeer.Freeze() },
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

        // Create and add a pending inbound direct invite
        using var mainIdentityEcdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var mainIdentityPriv = mainIdentityEcdh.ExportECPrivateKey();
        using var mainIdentityEcdsa = ECDsa.Create(mainIdentityEcdh.ExportParameters(true));
        var mainIdentitySpki = mainIdentityEcdsa.ExportSubjectPublicKeyInfo();

        using var mainSignedPreKey = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var mainSignedPreKeySpki = mainSignedPreKey.PublicKey.ExportSubjectPublicKeyInfo();
        var mainSignedPreKeyPriv = mainSignedPreKey.ExportECPrivateKey();

        var mainPayloadSig = mainIdentityEcdsa.SignData(mainSignedPreKeySpki, HashAlgorithmName.SHA256);

        var payload = new InviteHandshakeRequestPayload
        {
            Version = 1,
            InviterHost = "127.0.0.1",
            InviterPort = 5002,
            ExpiresAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow.AddMinutes(10)),
            RequestCorrelationId = correlationId.ToString(),
            InviterPreKey = new InviteHandshakePreKeyBundle
            {
                Version = 1,
                InviterSignedPreKey = ByteString.CopyFrom(mainSignedPreKeySpki),
                PreKeySignature = ByteString.CopyFrom(mainPayloadSig)
            }
        };

        var payloadBytes = payload.ToByteArray();
        var payloadSig = mainIdentityEcdsa.SignData(payloadBytes, HashAlgorithmName.SHA256);

        var request = new EstablishDirectSessionRequest
        {
            Version = 1,
            InviterIdentityKey = ByteString.CopyFrom(mainIdentitySpki),
            Payload = ByteString.CopyFrom(payloadBytes),
            PayloadSignature = ByteString.CopyFrom(payloadSig)
        };

        // Manually add pending invite to simulate the queued state
        var peer = sut.Peers.Single(p => p.NetworkPeerId == simulatedPeerId);
        peer.AddPendingInboundDirectInvite(correlationId, request.ToByteArray(), mainIdentitySpki, mainPeerId);

        // Act
        await sut.RejectPendingInboundDirectInviteAsync(
            simulatedPeerId,
            correlationId,
            CancellationToken.None);

        // Assert
        // No session should be created
        peer.Sessions.Count.Should().Be(0);

        // Pending invite should be removed
        peer.PendingInboundDirectInvites.Count.Should().Be(0);
    }
}

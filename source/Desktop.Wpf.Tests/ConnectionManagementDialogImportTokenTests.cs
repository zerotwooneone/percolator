using System;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Desktop.Wpf.Features.Sessions;
using FluentAssertions;
using Google.Protobuf;
using Moq;
using NUnit.Framework;
using Percolator.Application.Identity;
using Percolator.Application.Network;
using Percolator.Application.Network.Handshake;
using Percolator.Application.Services;
using Percolator.Contracts;
using Percolator.Cryptography;
using Percolator.Network;

namespace Desktop.Wpf.Tests;

[TestFixture]
[Apartment(ApartmentState.STA)]
public sealed class ConnectionManagementDialogImportTokenTests
{
    [Test]
    public async Task DecodeAndInitiate_GivenValidSignedToken_QueuesPendingInvitationAndSelectsIncomingTab()
    {
        // Arrange
        WpfTestHarness.EnsureApplication();

        var inbox = new Mock<IMainInvitationInbox>(MockBehavior.Loose);
        inbox.Setup(x => x.GetOpenAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<PendingInvitationDto>());

        var actions = new Mock<IMainInvitationActions>(MockBehavior.Loose);
        var inboxEvents = new Mock<IMainInvitationInboxEvents>(MockBehavior.Loose);
        inboxEvents.SetupGet(x => x.Changed).Returns(new R3.Subject<R3.Unit>());

        var reverseSignalInvites = new Mock<IMainReverseSignalInviteFactory>(MockBehavior.Loose);
        var grpcSessions = new Mock<IGrpcSessionService>(MockBehavior.Loose);
        var transport = new Mock<IMessageTransportService>(MockBehavior.Loose);
        var secureMessaging = new Mock<ISecureMessagingService>(MockBehavior.Loose);
        var directSessions = new Mock<IDirectSessionRepository>(MockBehavior.Loose);
        directSessions.Setup(x => x.ListAsync(It.IsAny<int>()))
            .ReturnsAsync(Array.Empty<DirectSession>());

        var simulatorState = new Mock<Desktop.Wpf.Features.Simulator.ISimulatorStateService>(MockBehavior.Loose);
        simulatorState.Setup(x => x.InitializeAsync(It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var active = new ActiveIdentityContext();
        active.SetActiveIdentity(new Percolator.Identity.Model.IdentityRecord(Guid.NewGuid(), "self"));

        var peerIdentities = new Mock<Percolator.Identity.IPeerIdentityRepository>(MockBehavior.Loose);

        var store = new Desktop.Wpf.Features.Sessions.State.SecureChannelsStore();

        var sessionCrypto = new Mock<ISessionCrypto>(MockBehavior.Loose);
        var preHandshake = new Mock<IPreHandshakeSessionStore>(MockBehavior.Loose);
        var sentInvitations = new Mock<ISentInvitationRepository>(MockBehavior.Loose);
        var clock = new Mock<IClock>(MockBehavior.Loose);
        clock.SetupGet(x => x.UtcNow).Returns(DateTimeOffset.UtcNow);
        var mediator = new Mock<MediatR.IMediator>(MockBehavior.Loose);

        var establish = new Mock<IEstablishDirectSessionService>(MockBehavior.Loose);
        establish.Setup(x => x.QueueInviteAsync(
                It.IsAny<Percolator.Identity.SelfId>(),
                It.IsAny<byte[]>(),
                It.IsAny<byte[]>(),
                It.IsAny<byte[]>(),
                It.IsAny<bool>(),
                It.IsAny<Percolator.Identity.PeerId?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Percolator.Cryptography.Primitives.RequestCorrelationId(Guid.NewGuid()));

        var sut = new ConnectionManagementDialogViewModel(
            inbox.Object,
            actions.Object,
            inboxEvents.Object,
            reverseSignalInvites.Object,
            grpcSessions.Object,
            transport.Object,
            secureMessaging.Object,
            directSessions.Object,
            simulatorState.Object,
            active,
            peerIdentities.Object,
            establish.Object,
            store,
            sessionCrypto.Object,
            preHandshake.Object,
            sentInvitations.Object,
            clock.Object,
            mediator.Object);

        var token = CreateSignedInviteToken(out var inviterSpki, out var payloadBytes, out var sigBytes);
        sut.InviteTokenText.Value = token;

        // Act
        sut.DecodeAndInitiateCommand.Execute(null);

        // Assert
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        while (!cts.IsCancellationRequested)
        {
            var count = establish.Invocations.Count;
            if (count > 0) break;
            await Task.Delay(20, cts.Token);
        }

        establish.Invocations.Count.Should().BeGreaterThan(0);

        establish.Verify(x => x.QueueInviteAsync(
                It.IsAny<Percolator.Identity.SelfId>(),
                It.Is<byte[]>(b => b != null && b.SequenceEqual(inviterSpki)),
                It.Is<byte[]>(b => b != null && b.SequenceEqual(payloadBytes)),
                It.Is<byte[]>(b => b != null && b.SequenceEqual(sigBytes)),
                false,
                null,
                It.IsAny<CancellationToken>()),
            Times.Once);

        sut.SelectedTabIndex.Value.Should().Be(0);
    }

    [Test]
    public async Task DecodeAndInitiate_GivenInvalidSignature_DoesNotQueueAndShowsError()
    {
        // Arrange
        WpfTestHarness.EnsureApplication();

        var inbox = new Mock<IMainInvitationInbox>(MockBehavior.Loose);
        inbox.Setup(x => x.GetOpenAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<PendingInvitationDto>());

        var actions = new Mock<IMainInvitationActions>(MockBehavior.Loose);
        var inboxEvents = new Mock<IMainInvitationInboxEvents>(MockBehavior.Loose);
        inboxEvents.SetupGet(x => x.Changed).Returns(new R3.Subject<R3.Unit>());

        var reverseSignalInvites = new Mock<IMainReverseSignalInviteFactory>(MockBehavior.Loose);
        var grpcSessions = new Mock<IGrpcSessionService>(MockBehavior.Loose);
        var transport = new Mock<IMessageTransportService>(MockBehavior.Loose);
        var secureMessaging = new Mock<ISecureMessagingService>(MockBehavior.Loose);
        var directSessions = new Mock<IDirectSessionRepository>(MockBehavior.Loose);
        directSessions.Setup(x => x.ListAsync(It.IsAny<int>()))
            .ReturnsAsync(Array.Empty<DirectSession>());

        var simulatorState = new Mock<Desktop.Wpf.Features.Simulator.ISimulatorStateService>(MockBehavior.Loose);
        simulatorState.Setup(x => x.InitializeAsync(It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var active = new ActiveIdentityContext();
        active.SetActiveIdentity(new Percolator.Identity.Model.IdentityRecord(Guid.NewGuid(), "self"));

        var peerIdentities = new Mock<Percolator.Identity.IPeerIdentityRepository>(MockBehavior.Loose);
        var establish = new Mock<IEstablishDirectSessionService>(MockBehavior.Loose);

        var store = new Desktop.Wpf.Features.Sessions.State.SecureChannelsStore();

        var sessionCrypto = new Mock<ISessionCrypto>(MockBehavior.Loose);
        var preHandshake = new Mock<IPreHandshakeSessionStore>(MockBehavior.Loose);
        var sentInvitations = new Mock<ISentInvitationRepository>(MockBehavior.Loose);
        var clock = new Mock<IClock>(MockBehavior.Loose);
        clock.SetupGet(x => x.UtcNow).Returns(DateTimeOffset.UtcNow);
        var mediator = new Mock<MediatR.IMediator>(MockBehavior.Loose);

        var sut = new ConnectionManagementDialogViewModel(
            inbox.Object,
            actions.Object,
            inboxEvents.Object,
            reverseSignalInvites.Object,
            grpcSessions.Object,
            transport.Object,
            secureMessaging.Object,
            directSessions.Object,
            simulatorState.Object,
            active,
            peerIdentities.Object,
            establish.Object,
            store,
            sessionCrypto.Object,
            preHandshake.Object,
            sentInvitations.Object,
            clock.Object,
            mediator.Object);

        var token = CreateSignedInviteToken(out _, out var payloadBytes, out var sigBytes);

        // Corrupt only the signature bytes to keep protobuf parsing valid.
        var env = EstablishDirectSessionRequest.Parser.ParseFrom(Convert.FromBase64String(token));
        var corruptedSig = (byte[])sigBytes.Clone();
        corruptedSig[0] = (byte)(corruptedSig[0] ^ 0x01);
        var corruptedEnv = new EstablishDirectSessionRequest
        {
            Version = env.Version,
            InviterIdentityKey = env.InviterIdentityKey,
            Payload = env.Payload,
            PayloadSignature = ByteString.CopyFrom(corruptedSig)
        };
        sut.InviteTokenText.Value = Convert.ToBase64String(corruptedEnv.ToByteArray());

        // Act
        sut.DecodeAndInitiateCommand.Execute(null);

        // Assert
        await Task.Delay(100);
        establish.Verify(x => x.QueueInviteAsync(
                It.IsAny<Percolator.Identity.SelfId>(),
                It.IsAny<byte[]>(),
                It.IsAny<byte[]>(),
                It.IsAny<byte[]>(),
                It.IsAny<bool>(),
                It.IsAny<Percolator.Identity.PeerId?>(),
                It.IsAny<CancellationToken>()),
            Times.Never);

        sut.ErrorText.Value.Should().Be("Token signature invalid.");
    }

    private static string CreateSignedInviteToken(out byte[] inviterIdentitySpki, out byte[] payloadBytes, out byte[] signatureBytes)
    {
        using var inviterEcdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        inviterIdentitySpki = inviterEcdsa.ExportSubjectPublicKeyInfo();

        var payload = new InviteHandshakeRequestPayload
        {
            Version = 1,
            InviterHost = "127.0.0.1",
            InviterPort = 5002,
            RequestCorrelationId = Guid.NewGuid().ToString()
        };

        payloadBytes = payload.ToByteArray();
        signatureBytes = inviterEcdsa.SignData(payloadBytes, HashAlgorithmName.SHA256);

        var env = new EstablishDirectSessionRequest
        {
            Version = 1,
            InviterIdentityKey = ByteString.CopyFrom(inviterIdentitySpki),
            Payload = ByteString.CopyFrom(payloadBytes),
            PayloadSignature = ByteString.CopyFrom(signatureBytes)
        };

        return Convert.ToBase64String(env.ToByteArray());
    }
}

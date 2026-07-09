using System;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Desktop.Wpf.Features.Self;
using Desktop.Wpf.Features.Sessions;
using Desktop.Wpf.Features.Sessions.Commands;
using FluentAssertions;
using Google.Protobuf;
using Moq;
using NUnit.Framework;
using Percolator.Application.Identity;
using Percolator.Contracts;
using Percolator.Cryptography;
using Percolator.Network;
using Microsoft.Extensions.DependencyInjection;

namespace Desktop.Wpf.Tests;

[TestFixture]
[Apartment(ApartmentState.STA)]
public sealed class ConnectionManagementDialogImportTokenTests
{
    [Test]
    public async Task DecodeAndInitiate_GivenValidSignedToken_QueuesPendingInvitationAndSelectsIncomingTab()
    {
        // ARRANGE
        WpfTestHarness.EnsureApplication();

        var directSessions = new Mock<IDirectSessionRepository>(MockBehavior.Loose);
        directSessions.Setup(x => x.ListAsync(It.IsAny<Percolator.Network.NetworkSelfId>()))
            .ReturnsAsync(Array.Empty<DirectSession>());

        var active = new ActiveIdentityContext();
        active.SetActiveIdentity(new Percolator.Identity.Model.IdentityRecord(new Percolator.Identity.SelfId(1), new Percolator.Identity.PublicIdentityId(Guid.NewGuid()), new Percolator.Identity.DeviceId(1), "self"));

        var state = new PeerConnectionStateService(Mock.Of<IServiceScopeFactory>(MockBehavior.Loose));
        var identityStateService = new Mock<IIdentityStateService>(MockBehavior.Loose);
        var ui = new TestUiDispatcher();

        var clock = new Mock<IClock>(MockBehavior.Loose);
        clock.SetupGet(x => x.UtcNow).Returns(DateTimeOffset.UtcNow);
        var mediator = new Mock<MediatR.IMediator>(MockBehavior.Loose);
        mediator.Setup(x => x.Send(It.IsAny<DecodeAndQueueInviteCommand>(), default))
            .ReturnsAsync(new DecodeAndQueueInviteResult.Success());

        var sut = new ConnectionManagementDialogViewModel(
            active,
            state,
            identityStateService.Object,
            ui,
            mediator.Object);

        var token = CreateSignedInviteToken(out var inviterSpki, out var payloadBytes, out var sigBytes);
        sut.InviteTokenText.Value = token;

        // ACT
        sut.DecodeAndInitiateCommand.Execute(null);

        // ASSERT: Tab selected (observable state change)
        sut.SelectedTabIndex.Value.Should().Be(0);
    }

    [Test]
    public async Task DecodeAndInitiate_GivenInvalidSignature_DoesNotQueueAndShowsError()
    {
        // ARRANGE
        WpfTestHarness.EnsureApplication();

        var directSessions = new Mock<IDirectSessionRepository>(MockBehavior.Loose);
        directSessions.Setup(x => x.ListAsync(It.IsAny<Percolator.Network.NetworkSelfId>()))
            .ReturnsAsync(Array.Empty<DirectSession>());

        var active = new ActiveIdentityContext();
        active.SetActiveIdentity(new Percolator.Identity.Model.IdentityRecord(new Percolator.Identity.SelfId(1), new Percolator.Identity.PublicIdentityId(Guid.NewGuid()), new Percolator.Identity.DeviceId(1), "self"));

        var state = new PeerConnectionStateService(Mock.Of<IServiceScopeFactory>(MockBehavior.Loose));
        var identityStateService = new Mock<IIdentityStateService>(MockBehavior.Loose);
        var ui = new TestUiDispatcher();

        var mediator = new Mock<MediatR.IMediator>(MockBehavior.Loose);
        mediator.Setup(x => x.Send(It.IsAny<DecodeAndQueueInviteCommand>(), default))
            .ReturnsAsync(new DecodeAndQueueInviteResult.Failed("Token signature invalid."));

        var sut = new ConnectionManagementDialogViewModel(
            active,
            state,
            identityStateService.Object,
            ui,
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

        // ACT
        sut.DecodeAndInitiateCommand.Execute(null);

        // ASSERT: Error text is set (observable state change)
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

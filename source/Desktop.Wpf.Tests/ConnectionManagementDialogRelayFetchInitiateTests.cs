using System;
using System.Linq;
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
using Percolator.Identity;
using Percolator.Identity.Model;
using Percolator.Network;

namespace Desktop.Wpf.Tests;

[TestFixture]
[Apartment(ApartmentState.STA)]
public sealed class ConnectionManagementDialogRelayFetchInitiateTests
{
    [Test]
    public async Task SearchAndConnect_RelayMode_GivenBundleFound_EnqueuesHandshakeHelloToRelayHost()
    {
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

        var relayHostId = Guid.NewGuid();
        var directSessionId = DirectSessionId.NewId();
        directSessions.Setup(x => x.GetByRemotePeerIdAsync(It.IsAny<Percolator.Network.PeerId>(), It.IsAny<int>()))
            .ReturnsAsync(new DirectSession(new Percolator.Network.PeerId(relayHostId), directSessionId));
        directSessions.Setup(x => x.ListAsync(It.IsAny<int>()))
            .ReturnsAsync(Array.Empty<DirectSession>());

        var active = new ActiveIdentityContext();
        active.SetActiveIdentity(
            new IdentityRecord(Guid.NewGuid(), "self") { SelfIdentityId = new SelfId(1) },
            new X3dhKeys(
                ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256),
                ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256)));

        var peerIdentities = new Mock<Percolator.Identity.IPeerIdentityRepository>(MockBehavior.Loose);
        var establish = new Mock<IEstablishDirectSessionService>(MockBehavior.Loose);
        var store = new Desktop.Wpf.Features.Sessions.State.SecureChannelsStore();

        var sessionCrypto = new Mock<ISessionCrypto>(MockBehavior.Loose);
        sessionCrypto.Setup(x => x.VerifySignature(It.IsAny<RatchetIdentityKey>(), It.IsAny<PreKey>(), It.IsAny<Percolator.Cryptography.Signature>()))
            .Returns(true);
        sessionCrypto.Setup(x => x.X3DH_Initiate(It.IsAny<PrivatePreKey>(), It.IsAny<Percolator.Cryptography.PreKeyBundle>()))
            .Returns((new SharedSecret(new byte[32]), new RatchetEphemeralKey(new byte[32])));

        var preHandshake = new Mock<IPreHandshakeSessionStore>(MockBehavior.Loose);
        var sentInvitations = new Mock<ISentInvitationRepository>(MockBehavior.Loose);

        var clock = new Mock<IClock>(MockBehavior.Loose);
        clock.SetupGet(x => x.UtcNow).Returns(DateTimeOffset.UtcNow);

        var mediator = new Mock<MediatR.IMediator>(MockBehavior.Loose);

        byte[] remoteIdentitySpki;
        using (var remoteEcdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256))
        {
            remoteIdentitySpki = remoteEcdsa.ExportSubjectPublicKeyInfo();
        }

        var targetPkh = SHA256.HashData(remoteIdentitySpki);
        var targetPkhHex = Convert.ToHexString(targetPkh);

        var bundleResp = new InternalEnvelope
        {
            GetPreKeyBundleResponse = new GetPreKeyBundleResponse
            {
                Version = 1,
                PreKeyBundle = new GetPreKeyBundleResponse.Types.PreKeyBundle
                {
                    Version = 1,
                    IdentityKey = ByteString.CopyFrom(remoteIdentitySpki),
                    SignedPreKeyId = ByteString.CopyFrom(Guid.NewGuid().ToByteArray()),
                    SignedPreKey = ByteString.CopyFrom(new byte[] { 1, 2, 3 }),
                    PreKeySignature = ByteString.CopyFrom(new byte[] { 4, 5, 6 })
                }
            }
        };

        secureMessaging.Setup(x => x.DecryptInboundAsync(
                It.IsAny<int>(),
                It.IsAny<SessionRatchetMessage>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((new Percolator.Cryptography.SessionId(Guid.NewGuid()), new Plaintext(bundleResp.ToByteArray())));

        var encryptCall = 0;
        byte[]? enqueuePlaintextBytes = null;
        secureMessaging.Setup(x => x.EncryptAsync(
                It.IsAny<Percolator.Cryptography.SessionId>(),
                It.IsAny<Plaintext>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((Percolator.Cryptography.SessionId _, Plaintext pt, CancellationToken __) =>
            {
                encryptCall++;
                if (encryptCall == 2)
                {
                    enqueuePlaintextBytes = pt.Value;
                }
                return new SessionRatchetMessage(new byte[] { (byte)encryptCall });
            });

        var transportCall = 0;
        transport.Setup(x => x.SendMessageAsync(
                It.IsAny<Percolator.Identity.PeerId>(),
                It.IsAny<DirectSessionId>(),
                It.IsAny<SessionRatchetMessage>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                transportCall++;
                if (transportCall == 1)
                {
                    return new DeliverOpaqueMessageResponse
                    {
                        Version = 1,
                        ResponsePayload = new DeliverOpaqueMessageResponse.Types.Payload
                        {
                            Version = 1,
                            ResponsePayload = ByteString.CopyFrom(new byte[] { 9, 9, 9 })
                        }
                    };
                }
                return new DeliverOpaqueMessageResponse { Version = 1 };
            });

        var sut = new ConnectionManagementDialogViewModel(
            inbox.Object,
            actions.Object,
            inboxEvents.Object,
            reverseSignalInvites.Object,
            grpcSessions.Object,
            transport.Object,
            secureMessaging.Object,
            directSessions.Object,
            active,
            peerIdentities.Object,
            establish.Object,
            store,
            sessionCrypto.Object,
            preHandshake.Object,
            sentInvitations.Object,
            clock.Object,
            mediator.Object);

        sut.SelectedRouteMode.Value = new RouteModeOption("relay", "Via Relay Host");
        sut.SelectedRelayHost.Value = new RelayHostOption(relayHostId, "relay");
        sut.TargetPkhText.Value = targetPkhHex;

        sut.SearchAndConnectCommand.Execute(null);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        while (!cts.IsCancellationRequested)
        {
            if (transport.Invocations.Count >= 2 && enqueuePlaintextBytes is not null) break;
            await Task.Delay(20, cts.Token);
        }

        transport.Verify(x => x.SendMessageAsync(
                It.Is<Percolator.Identity.PeerId>(p => p.Value == relayHostId),
                It.Is<DirectSessionId>(sid => sid.Value == directSessionId.Value),
                It.IsAny<SessionRatchetMessage>(),
                It.IsAny<CancellationToken>()),
            Times.Exactly(2));

        enqueuePlaintextBytes.Should().NotBeNull();
        var parsed = InternalEnvelope.Parser.ParseFrom(enqueuePlaintextBytes!);
        parsed.ApplicationPayloadCase.Should().Be(InternalEnvelope.ApplicationPayloadOneofCase.MessageQueueEnvelope);
        parsed.MessageQueueEnvelope.Should().NotBeNull();
        parsed.MessageQueueEnvelope.MessageCase.Should().Be(MessageQueueEnvelope.MessageOneofCase.EnqueueOpaqueMessageRequest);

        var enqueue = parsed.MessageQueueEnvelope.EnqueueOpaqueMessageRequest;
        enqueue.RecipientPublicKeyHash.ToByteArray().Should().Equal(targetPkh);
        enqueue.MessageBlob.Should().NotBeNull();
        enqueue.MessageBlob.Length.Should().BeGreaterThan(0);

        var hello = HandshakeInitiatorHello.Parser.ParseFrom(enqueue.MessageBlob);
        hello.Version.Should().Be(1);

        preHandshake.Verify(x => x.SaveAsync(It.IsAny<PreHandshakeRecord>(), It.IsAny<CancellationToken>()), Times.Once);
        sentInvitations.Verify(x => x.UpsertAsync(It.IsAny<SentInvitation>(), It.IsAny<CancellationToken>()), Times.Once);
    }
}

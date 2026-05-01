using System.Net;
using System.Security.Cryptography;
using FluentAssertions;
using Google.Protobuf;
using MediatR;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Percolator.Application.Identity;
using Percolator.Application.Network;
using Percolator.Contracts;
using Percolator.Cryptography;
using Percolator.Cryptography.Primitives;
using Percolator.Identity;
using Percolator.Identity.Model;
using Percolator.Network;
using Percolator.Application.ReverseSignal;
using Percolator.Application.Services;
using Percolator.ApplicationTests.Services;

namespace Percolator.ApplicationTests.ReverseSignal
{
    [TestFixture]
    public class ApprovePendingSessionCommandTests
    {
        private sealed class ActiveAccessorStub : IActiveIdentityAccessor
        {
            public bool IsActive { get; set; }
        }

        private static PendingSession BuildPending(
            PendingSessionId id,
            Percolator.Cryptography.Primitives.PeerId inviterPeerId,
            RequestCorrelationId correlationId,
            byte[] inviterIdentitySpki,
            byte[] payloadBytes,
            byte[] payloadSigBytes,
            TestClock clock)
        {
            var invitationEnvelope = new EstablishDirectSessionRequest
            {
                Version = 1,
                InviterIdentityKey = ByteString.CopyFrom(inviterIdentitySpki),
                Payload = ByteString.CopyFrom(payloadBytes),
                PayloadSignature = ByteString.CopyFrom(payloadSigBytes)
            };

            return PendingSession.FromInvitationWithMetadata(
                id,
                inviterPeerId,
                new ProtocolVersion(1),
                HandshakeInvitation.FromBytes(invitationEnvelope.ToByteArray()),
                requestCorrelationId: correlationId,
                isRelayed: false,
                relayHostPeerId: null,
                inviterIdentityKey: RatchetIdentityKey.FromBytes(inviterIdentitySpki),
                callbackEndpointHost: "example.com",
                callbackEndpointPort: 7777,
                clock,
                expiresAtUtc: clock.UtcNow.AddMinutes(10));
        }

        [Test]
        public async Task ApprovePendingSession_WhenNotReady_ReturnsRejectedNotReady_AndDoesNotMutate()
        {
            var logger = NullLogger<ApprovePendingSessionHandler>.Instance;
            var activeAccessor = new ActiveAccessorStub { IsActive = false };
            var active = new ActiveIdentityContext();
            var pendingRepo = new Mock<IPendingSessionRepository>(MockBehavior.Strict);

            var sut = new ApprovePendingSessionHandler(
                logger,
                activeAccessor,
                active,
                pendingRepo.Object,
                new TestClock (),
                Mock.Of<ICallbackEndpointValidator>(),
                Mock.Of<ISessionCrypto>(),
                Mock.Of<IHandshakePlanner>(),
                Mock.Of<ISessionRepository>(),
                Mock.Of<IPeerRoutingProfileRepository>(),
                Mock.Of<IInviteHandshakeResponseDeliveryService>(),
                Mock.Of<IDirectSessionLocator>(),
                Mock.Of<IDirectSessionMappingWriter>(),
                Mock.Of<ISecureMessagingService>(),
                Mock.Of<IMessageTransportService>(),
                Mock.Of<IMediator>());

            var result = await sut.Handle(new ApprovePendingSessionCommand(PendingSessionId.NewId()), CancellationToken.None);
            result.Should().BeOfType<ApprovePendingSessionResult.RejectedNotReady>();
            pendingRepo.VerifyNoOtherCalls();
        }

        [Test]
        public async Task ApprovePendingSession_WhenSendFails_KeepsPending_AndReturnsFailed()
        {
            var clock = new TestClock ();
            var logger = NullLogger<ApprovePendingSessionHandler>.Instance;
            var activeAccessor = new ActiveAccessorStub { IsActive = true };
            var active = new ActiveIdentityContext();
            var identity = new IdentityRecord(Guid.NewGuid(), "self") { SelfIdentityId = new SelfId(1) };
            var keys = new X3dhKeys(
                ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256),
                ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256));
            active.SetActiveIdentity(identity, keys);

            var correlation = new RequestCorrelationId(Guid.NewGuid());

            using var inviterEcdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
            var inviterSpki = inviterEcdh.ExportSubjectPublicKeyInfo();

            var payload = new InviteHandshakeRequestPayload
            {
                Version = 1,
                InviterHost = "example.com",
                InviterPort = 7777,
                ExpiresAtUtc = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTimeOffset(clock.UtcNow.AddMinutes(10)),
                RequestCorrelationId = correlation.ToString(),
                InviterPreKey = new InviteHandshakePreKeyBundle
                {
                    Version = 1,
                    InviterSignedPreKey = ByteString.CopyFrom(inviterEcdh.ExportSubjectPublicKeyInfo()),
                    PreKeySignature = ByteString.CopyFrom(new byte[64])
                }
            };

            var pendingId = PendingSessionId.NewId();
            var pending = BuildPending(
                pendingId,
                new Percolator.Cryptography.Primitives.PeerId(Guid.NewGuid()),
                correlation,
                inviterSpki,
                payload.ToByteArray(),
                new byte[64],
                clock);

            var pendingRepo = new Mock<IPendingSessionRepository>(MockBehavior.Strict);
            pendingRepo.Setup(r => r.GetAsync(pendingId, It.IsAny<CancellationToken>()))
                .ReturnsAsync(pending);

            pendingRepo.Setup(r => r.DeleteAsync(pendingId, It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);

            var callbackValidator = new Mock<ICallbackEndpointValidator>(MockBehavior.Strict);
            callbackValidator.Setup(v => v.Validate("example.com", 7777))
                .Returns(new CallbackEndpointValidationResult(true, null, false, false));

            var planner = new Mock<IHandshakePlanner>(MockBehavior.Strict);
            planner.Setup(p => p.ValidatePreKeyBundle(It.IsAny<Percolator.Cryptography.PreKeyBundle>()));

            var sessionCrypto = new Mock<ISessionCrypto>(MockBehavior.Strict);
            sessionCrypto.Setup(c => c.X3DH_Initiate(It.IsAny<PrivatePreKey>(), It.IsAny<Percolator.Cryptography.PreKeyBundle>()))
                .Returns((PrivatePreKey _, Percolator.Cryptography.PreKeyBundle _) => (
                    SharedSecret.FromBytes(new byte[32]),
                    RatchetEphemeralKey.FromBytes(new byte[64])
                ));

            var sessionRepo = new Mock<ISessionRepository>(MockBehavior.Strict);
            sessionRepo.Setup(r => r.AddAsync(It.IsAny<SecureSession>(), It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);

            var profileRepo = new Mock<IPeerRoutingProfileRepository>(MockBehavior.Strict);
            profileRepo.Setup(r => r.GetByIdAsync(It.IsAny<Percolator.Network.PeerId>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((PeerRoutingProfile?)null);
            profileRepo.Setup(r => r.UpsertAsync(It.IsAny<PeerRoutingProfile>(), It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);

            var delivery = new Mock<IInviteHandshakeResponseDeliveryService>(MockBehavior.Strict);
            delivery.Setup(d => d.DeliverAsync(
                    It.IsAny<Percolator.Network.PeerId>(),
                    It.IsAny<DnsEndPoint?>(),
                    It.IsAny<InviteHandshakeResponse>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(new InviteHandshakeResponseDeliveryResult(false, "Direct", new Exception("network")));

            var mediator = new Mock<IMediator>(MockBehavior.Strict);
            mediator
                .Setup(m => m.Publish(
                    It.IsAny<SecureSessionCreatedNotification>(),
                    It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);

            var sut = new ApprovePendingSessionHandler(
                logger,
                activeAccessor,
                active,
                pendingRepo.Object,
                clock,
                callbackValidator.Object,
                sessionCrypto.Object,
                planner.Object,
                sessionRepo.Object,
                profileRepo.Object,
                delivery.Object,
                Mock.Of<IDirectSessionLocator>(),
                Mock.Of<IDirectSessionMappingWriter>(),
                Mock.Of<ISecureMessagingService>(),
                Mock.Of<IMessageTransportService>(),
                mediator.Object);

            var result = await sut.Handle(new ApprovePendingSessionCommand(pendingId), CancellationToken.None);
            result.Should().BeOfType<ApprovePendingSessionResult.Failed>();
            pendingRepo.Verify(r => r.DeleteAsync(It.IsAny<PendingSessionId>(), It.IsAny<CancellationToken>()), Times.Never);
        }

        [Test]
        public async Task ApprovePendingSession_OnSuccess_DeletesPending_AndReturnsAccepted()
        {
            var clock = new TestClock ();
            var logger = NullLogger<ApprovePendingSessionHandler>.Instance;
            var activeAccessor = new ActiveAccessorStub { IsActive = true };
            var active = new ActiveIdentityContext();
            var identity = new IdentityRecord(Guid.NewGuid(), "self") { SelfIdentityId = new SelfId(1) };
            var keys = new X3dhKeys(
                ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256),
                ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256));
            active.SetActiveIdentity(identity, keys);

            var correlation = new RequestCorrelationId(Guid.NewGuid());

            using var inviterEcdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
            var inviterSpki = inviterEcdh.ExportSubjectPublicKeyInfo();

            var payload = new InviteHandshakeRequestPayload
            {
                Version = 1,
                InviterHost = "example.com",
                InviterPort = 7777,
                ExpiresAtUtc = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTimeOffset(clock.UtcNow.AddMinutes(10)),
                RequestCorrelationId = correlation.ToString(),
                InviterPreKey = new InviteHandshakePreKeyBundle
                {
                    Version = 1,
                    InviterSignedPreKey = ByteString.CopyFrom(inviterEcdh.ExportSubjectPublicKeyInfo()),
                    PreKeySignature = ByteString.CopyFrom(new byte[64])
                }
            };

            var pendingId = PendingSessionId.NewId();
            var pending = BuildPending(
                pendingId,
                new Percolator.Cryptography.Primitives.PeerId(Guid.NewGuid()),
                correlation,
                inviterSpki,
                payload.ToByteArray(),
                new byte[64],
                clock);

            var pendingRepo = new Mock<IPendingSessionRepository>(MockBehavior.Strict);
            pendingRepo.Setup(r => r.GetAsync(pendingId, It.IsAny<CancellationToken>()))
                .ReturnsAsync(pending);
            pendingRepo.Setup(r => r.DeleteAsync(pendingId, It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);

            var callbackValidator = new Mock<ICallbackEndpointValidator>(MockBehavior.Strict);
            callbackValidator.Setup(v => v.Validate("example.com", 7777))
                .Returns(new CallbackEndpointValidationResult(true, null, false, false));

            var planner = new Mock<IHandshakePlanner>(MockBehavior.Strict);
            planner.Setup(p => p.ValidatePreKeyBundle(It.IsAny<Percolator.Cryptography.PreKeyBundle>()));

            var sessionCrypto = new Mock<ISessionCrypto>(MockBehavior.Strict);
            sessionCrypto.Setup(c => c.X3DH_Initiate(It.IsAny<PrivatePreKey>(), It.IsAny<Percolator.Cryptography.PreKeyBundle>()))
                .Returns((PrivatePreKey _, Percolator.Cryptography.PreKeyBundle _) => (
                    SharedSecret.FromBytes(new byte[32]),
                    RatchetEphemeralKey.FromBytes(new byte[64])
                ));

            var sessionRepo = new Mock<ISessionRepository>(MockBehavior.Strict);
            sessionRepo.Setup(r => r.AddAsync(It.IsAny<SecureSession>(), It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);

            var profileRepo = new Mock<IPeerRoutingProfileRepository>(MockBehavior.Strict);
            profileRepo.Setup(r => r.GetByIdAsync(It.IsAny<Percolator.Network.PeerId>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((PeerRoutingProfile?)null);
            profileRepo.Setup(r => r.UpsertAsync(It.IsAny<PeerRoutingProfile>(), It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);

            var delivery = new Mock<IInviteHandshakeResponseDeliveryService>(MockBehavior.Strict);
            delivery.Setup(d => d.DeliverAsync(
                    It.IsAny<Percolator.Network.PeerId>(),
                    It.IsAny<DnsEndPoint?>(),
                    It.IsAny<InviteHandshakeResponse>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(new InviteHandshakeResponseDeliveryResult(true, "Direct"));

            var mediator = new Mock<IMediator>(MockBehavior.Strict);
            mediator
                .Setup(m => m.Publish(
                    It.IsAny<SecureSessionCreatedNotification>(),
                    It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);
            mediator
                .Setup(m => m.Publish(
                    It.IsAny<PendingSessionRemovedNotification>(),
                    It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);

            var sut = new ApprovePendingSessionHandler(
                logger,
                activeAccessor,
                active,
                pendingRepo.Object,
                clock,
                callbackValidator.Object,
                sessionCrypto.Object,
                planner.Object,
                sessionRepo.Object,
                profileRepo.Object,
                delivery.Object,
                Mock.Of<IDirectSessionLocator>(),
                Mock.Of<IDirectSessionMappingWriter>(),
                Mock.Of<ISecureMessagingService>(),
                Mock.Of<IMessageTransportService>(),
                mediator.Object);

            var result = await sut.Handle(new ApprovePendingSessionCommand(pendingId), CancellationToken.None);
            result.Should().BeOfType<ApprovePendingSessionResult.Accepted>();
            pendingRepo.Verify(r => r.DeleteAsync(pendingId, It.IsAny<CancellationToken>()), Times.Once);
        }

        [Test]
        public async Task ApprovePendingSession_WhenRelayed_DoesNotTouchCallbackOrRouting_AndReturnsAccepted()
        {
            var clock = new TestClock ();
            var logger = NullLogger<ApprovePendingSessionHandler>.Instance;
            var activeAccessor = new ActiveAccessorStub { IsActive = true };
            var active = new ActiveIdentityContext();
            var identity = new IdentityRecord(Guid.NewGuid(), "self") { SelfIdentityId = new SelfId(1) };
            var keys = new X3dhKeys(
                ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256),
                ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256));
            active.SetActiveIdentity(identity, keys);

            var correlation = new RequestCorrelationId(Guid.NewGuid());

            using var inviterEcdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
            var inviterSpki = inviterEcdh.ExportSubjectPublicKeyInfo();

            var payload = new InviteHandshakeRequestPayload
            {
                Version = 1,
                InviterHost = "example.com",
                InviterPort = 7777,
                ExpiresAtUtc = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTimeOffset(clock.UtcNow.AddMinutes(10)),
                RequestCorrelationId = correlation.ToString(),
                InviterPreKey = new InviteHandshakePreKeyBundle
                {
                    Version = 1,
                    InviterSignedPreKey = ByteString.CopyFrom(inviterEcdh.ExportSubjectPublicKeyInfo()),
                    PreKeySignature = ByteString.CopyFrom(new byte[64])
                }
            };

            var invitationEnvelope = new EstablishDirectSessionRequest
            {
                Version = 1,
                InviterIdentityKey = ByteString.CopyFrom(inviterSpki),
                Payload = ByteString.CopyFrom(payload.ToByteArray()),
                PayloadSignature = ByteString.CopyFrom(new byte[64])
            };

            var pendingId = PendingSessionId.NewId();
            var pending = PendingSession.FromInvitationWithMetadata(
                pendingId,
                new Percolator.Cryptography.Primitives.PeerId(Guid.NewGuid()),
                new ProtocolVersion(1),
                HandshakeInvitation.FromBytes(invitationEnvelope.ToByteArray()),
                requestCorrelationId: correlation,
                isRelayed: true,
                relayHostPeerId: new Percolator.Cryptography.Primitives.PeerId(Guid.NewGuid()),
                inviterIdentityKey: RatchetIdentityKey.FromBytes(inviterSpki),
                callbackEndpointHost: null,
                callbackEndpointPort: null,
                clock,
                expiresAtUtc: clock.UtcNow.AddMinutes(10));

            var pendingRepo = new Mock<IPendingSessionRepository>(MockBehavior.Strict);
            pendingRepo.Setup(r => r.GetAsync(pendingId, It.IsAny<CancellationToken>()))
                .ReturnsAsync(pending);

            pendingRepo.Setup(r => r.DeleteAsync(pendingId, It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);

            var callbackValidator = new Mock<ICallbackEndpointValidator>(MockBehavior.Strict);
            var planner = new Mock<IHandshakePlanner>(MockBehavior.Strict);
            var sessionCrypto = new Mock<ISessionCrypto>(MockBehavior.Strict);
            var sessionRepo = new Mock<ISessionRepository>(MockBehavior.Strict);
            var profileRepo = new Mock<IPeerRoutingProfileRepository>(MockBehavior.Strict);

            planner.Setup(p => p.ValidatePreKeyBundle(It.IsAny<Percolator.Cryptography.PreKeyBundle>()));
            sessionCrypto.Setup(c => c.X3DH_Initiate(It.IsAny<PrivatePreKey>(), It.IsAny<Percolator.Cryptography.PreKeyBundle>()))
                .Returns((PrivatePreKey _, Percolator.Cryptography.PreKeyBundle _) => (
                    SharedSecret.FromBytes(new byte[32]),
                    RatchetEphemeralKey.FromBytes(new byte[64])
                ));
            sessionRepo.Setup(r => r.AddAsync(It.IsAny<SecureSession>(), It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);

            // Relayed accept should not use IInviteHandshakeResponseDeliveryService; it should enqueue via relay transport.
            var delivery = new Mock<IInviteHandshakeResponseDeliveryService>(MockBehavior.Strict);

            var directSessions = new Mock<IDirectSessionLocator>(MockBehavior.Strict);
            directSessions.Setup(s => s.GetAsync(It.IsAny<Percolator.Identity.PeerId>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((Percolator.Network.DirectSessionId?)new Percolator.Network.DirectSessionId(Guid.NewGuid()));

            var secure = new Mock<ISecureMessagingService>(MockBehavior.Strict);
            secure.Setup(s => s.EncryptAsync(It.IsAny<Percolator.Cryptography.SessionId>(), It.IsAny<Plaintext>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(SessionRatchetMessage.FromBytes(new byte[] { 1, 2, 3 }));

            var transport = new Mock<IMessageTransportService>(MockBehavior.Strict);
            transport.Setup(t => t.SendMessageAsync(
                    It.IsAny<Percolator.Identity.PeerId>(),
                    It.IsAny<Percolator.Network.DirectSessionId>(),
                    It.IsAny<SessionRatchetMessage>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(new SendMessageResponse { OriginalResponse = new Percolator.Contracts.DeliverOpaqueMessageResponse() });

            var mediator = new Mock<IMediator>(MockBehavior.Strict);
            mediator
                .Setup(m => m.Publish(
                    It.IsAny<SecureSessionCreatedNotification>(),
                    It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);
            mediator
                .Setup(m => m.Publish(
                    It.IsAny<PendingSessionRemovedNotification>(),
                    It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);

            var sut = new ApprovePendingSessionHandler(
                logger,
                activeAccessor,
                active,
                pendingRepo.Object,
                clock,
                callbackValidator.Object,
                sessionCrypto.Object,
                planner.Object,
                sessionRepo.Object,
                profileRepo.Object,
                delivery.Object,
                directSessions.Object,
                Mock.Of<IDirectSessionMappingWriter>(),
                secure.Object,
                transport.Object,
                mediator.Object);

            var result = await sut.Handle(new ApprovePendingSessionCommand(pendingId), CancellationToken.None);
            result.Should().BeOfType<ApprovePendingSessionResult.Accepted>();

            pendingRepo.Verify(r => r.DeleteAsync(pendingId, It.IsAny<CancellationToken>()), Times.Once);
            callbackValidator.VerifyNoOtherCalls();
            profileRepo.VerifyNoOtherCalls();
        }
    }
}

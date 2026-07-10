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
            Percolator.Cryptography.Primitives.CryptoPeerId inviterCryptoPeerId,
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
                inviterCryptoPeerId,
                new ProtocolVersion(1),
                HandshakeInvitation.FromBytes(invitationEnvelope.ToByteArray()),
                requestCorrelationId: correlationId,
                isRelayed: false,
                relayHostPeerId: null,
                inviterIdentityKey: RatchetIdentityKey.FromBytes(inviterIdentitySpki),
                inviterPublicIdentityId: null,
                callbackEndpointHost: "example.com",
                callbackEndpointPort: 7777,
                clock,
                expiresAtUtc: clock.UtcNow.AddMinutes(10));
        }

        [Test]
        public async Task ApprovePendingSession_WhenNotReady_ReturnsRejectedNotReady()
        {
            var logger = NullLogger<ApprovePendingSessionHandler>.Instance;
            var activeAccessor = new ActiveAccessorStub { IsActive = false };
            var active = new ActiveIdentityContext();
            var pendingRepo = new Mock<IPendingSessionRepository>(MockBehavior.Loose);

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

            var result = await sut.Handle(new ApprovePendingSessionCommand(PendingSessionId.NewId(), new SelfId(1)), CancellationToken.None);
            result.Should().BeOfType<ApprovePendingSessionResult.RejectedNotReady>();
        }

        [Test]
        public async Task ApprovePendingSession_WhenSendFails_KeepsPending_AndReturnsFailed()
        {
            var clock = new TestClock ();
            var logger = NullLogger<ApprovePendingSessionHandler>.Instance;
            var activeAccessor = new ActiveAccessorStub { IsActive = true };
            var active = new ActiveIdentityContext();
            var identity = new IdentityRecord(new SelfId(1), new PublicIdentityId(Guid.NewGuid()), new Percolator.Identity.DeviceId(1), "self");
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
                new Percolator.Cryptography.Primitives.CryptoPeerId((uint)Random.Shared.Next(1, 1000000)),
                correlation,
                inviterSpki,
                payload.ToByteArray(),
                new byte[64],
                clock);

            var pendingRepo = new Mock<IPendingSessionRepository>(MockBehavior.Loose);
            pendingRepo.Setup(r => r.GetAsync(pendingId, It.IsAny<CryptoSelfId>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(pending);

            var callbackValidator = new Mock<ICallbackEndpointValidator>(MockBehavior.Loose);
            callbackValidator.Setup(v => v.Validate("example.com", 7777))
                .Returns(new CallbackEndpointValidationResult(true, null, false, false));

            var planner = new Mock<IHandshakePlanner>(MockBehavior.Loose);
            planner.Setup(p => p.ValidatePreKeyBundle(It.IsAny<Percolator.Cryptography.PreKeyBundle>()));

            var sessionCrypto = new Mock<ISessionCrypto>(MockBehavior.Loose);
            sessionCrypto.Setup(c => c.X3DH_Initiate(It.IsAny<PrivatePreKey>(), It.IsAny<Percolator.Cryptography.PreKeyBundle>()))
                .Returns((PrivatePreKey _, Percolator.Cryptography.PreKeyBundle _) => (
                    SharedSecret.FromBytes(new byte[32]),
                    RatchetEphemeralKey.FromBytes(new byte[64])
                ));

            var sessionRepo = new Mock<ISessionRepository>(MockBehavior.Loose);
            var profileRepo = new Mock<IPeerRoutingProfileRepository>(MockBehavior.Loose);
            var delivery = new Mock<IInviteHandshakeResponseDeliveryService>(MockBehavior.Loose);
            delivery.Setup(d => d.DeliverAsync(
                    It.IsAny<Percolator.Network.NetworkPeerId>(),
                    It.IsAny<DnsEndPoint?>(),
                    It.IsAny<InviteHandshakeResponse>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(new InviteHandshakeResponseDeliveryResult(false, "Direct", new Exception("network")));
            var mediator = new Mock<IMediator>(MockBehavior.Loose);

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

            var result = await sut.Handle(new ApprovePendingSessionCommand(pendingId, identity.SelfIdentityId), CancellationToken.None);
            result.Should().BeOfType<ApprovePendingSessionResult.Failed>();

            // Verify state: pending session should still exist
            var stillPending = await pendingRepo.Object.GetAsync(pendingId, new CryptoSelfId(1), CancellationToken.None);
            stillPending.Should().NotBeNull();
        }

        [Test]
        public async Task ApprovePendingSession_OnSuccess_DeletesPending_AndReturnsAccepted()
        {
            var clock = new TestClock ();
            var logger = NullLogger<ApprovePendingSessionHandler>.Instance;
            var activeAccessor = new ActiveAccessorStub { IsActive = true };
            var active = new ActiveIdentityContext();
            var identity = new IdentityRecord(new SelfId(1), new PublicIdentityId(Guid.NewGuid()), new Percolator.Identity.DeviceId(1), "self");
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
                new Percolator.Cryptography.Primitives.CryptoPeerId((uint)Random.Shared.Next(1, 1000000)),
                correlation,
                inviterSpki,
                payload.ToByteArray(),
                new byte[64],
                clock);

            var pendingRepo = new Mock<IPendingSessionRepository>(MockBehavior.Loose);
            pendingRepo.Setup(r => r.GetAsync(pendingId, It.IsAny<CryptoSelfId>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(pending);

            var callbackValidator = new Mock<ICallbackEndpointValidator>(MockBehavior.Loose);
            callbackValidator.Setup(v => v.Validate("example.com", 7777))
                .Returns(new CallbackEndpointValidationResult(true, null, false, false));

            var planner = new Mock<IHandshakePlanner>(MockBehavior.Loose);
            planner.Setup(p => p.ValidatePreKeyBundle(It.IsAny<Percolator.Cryptography.PreKeyBundle>()));

            var sessionCrypto = new Mock<ISessionCrypto>(MockBehavior.Loose);
            sessionCrypto.Setup(c => c.X3DH_Initiate(It.IsAny<PrivatePreKey>(), It.IsAny<Percolator.Cryptography.PreKeyBundle>()))
                .Returns((PrivatePreKey _, Percolator.Cryptography.PreKeyBundle _) => (
                    SharedSecret.FromBytes(new byte[32]),
                    RatchetEphemeralKey.FromBytes(new byte[64])
                ));

            var sessionRepo = new Mock<ISessionRepository>(MockBehavior.Loose);
            var profileRepo = new Mock<IPeerRoutingProfileRepository>(MockBehavior.Loose);
            var delivery = new Mock<IInviteHandshakeResponseDeliveryService>(MockBehavior.Loose);
            delivery.Setup(d => d.DeliverAsync(
                    It.IsAny<Percolator.Network.NetworkPeerId>(),
                    It.IsAny<DnsEndPoint?>(),
                    It.IsAny<InviteHandshakeResponse>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(new InviteHandshakeResponseDeliveryResult(true, "Direct"));
            var mediator = new Mock<IMediator>(MockBehavior.Loose);

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

            var result = await sut.Handle(new ApprovePendingSessionCommand(pendingId, identity.SelfIdentityId), CancellationToken.None);
            result.Should().BeOfType<ApprovePendingSessionResult.Accepted>();

            // Verify state: pending session should be deleted
            pendingRepo.Setup(r => r.GetAsync(pendingId, It.IsAny<CryptoSelfId>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((PendingSession?)null);
            var deleted = await pendingRepo.Object.GetAsync(pendingId, new CryptoSelfId(1), CancellationToken.None);
            deleted.Should().BeNull();
        }

        [Test]
        public async Task ApprovePendingSession_WhenRelayed_DoesNotTouchCallbackOrRouting_AndReturnsAccepted()
        {
            var clock = new TestClock ();
            var logger = NullLogger<ApprovePendingSessionHandler>.Instance;
            var activeAccessor = new ActiveAccessorStub { IsActive = true };
            var active = new ActiveIdentityContext();
            var identity = new IdentityRecord(new SelfId(1), new PublicIdentityId(Guid.NewGuid()), new Percolator.Identity.DeviceId(1), "self");
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
                new Percolator.Cryptography.Primitives.CryptoPeerId((uint)Random.Shared.Next(1, 1000000)),
                new ProtocolVersion(1),
                HandshakeInvitation.FromBytes(invitationEnvelope.ToByteArray()),
                requestCorrelationId: correlation,
                isRelayed: true,
                relayHostPeerId: new Percolator.Cryptography.Primitives.CryptoPeerId((uint)Random.Shared.Next(1, 1000000)),
                inviterIdentityKey: RatchetIdentityKey.FromBytes(inviterSpki),
                inviterPublicIdentityId: new CryptoPublicIdentityId(Guid.NewGuid()),
                callbackEndpointHost: null,
                callbackEndpointPort: null,
                clock,
                expiresAtUtc: clock.UtcNow.AddMinutes(10));

            var pendingRepo = new Mock<IPendingSessionRepository>(MockBehavior.Loose);
            pendingRepo.Setup(r => r.GetAsync(pendingId, It.IsAny<CryptoSelfId>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(pending);

            var callbackValidator = new Mock<ICallbackEndpointValidator>(MockBehavior.Loose);
            var planner = new Mock<IHandshakePlanner>(MockBehavior.Loose);
            planner.Setup(p => p.ValidatePreKeyBundle(It.IsAny<Percolator.Cryptography.PreKeyBundle>()));

            var sessionCrypto = new Mock<ISessionCrypto>(MockBehavior.Loose);
            sessionCrypto.Setup(c => c.X3DH_Initiate(It.IsAny<PrivatePreKey>(), It.IsAny<Percolator.Cryptography.PreKeyBundle>()))
                .Returns((PrivatePreKey _, Percolator.Cryptography.PreKeyBundle _) => (
                    SharedSecret.FromBytes(new byte[32]),
                    RatchetEphemeralKey.FromBytes(new byte[64])
                ));

            var sessionRepo = new Mock<ISessionRepository>(MockBehavior.Loose);
            var profileRepo = new Mock<IPeerRoutingProfileRepository>(MockBehavior.Loose);
            var delivery = new Mock<IInviteHandshakeResponseDeliveryService>(MockBehavior.Loose);
            var directSessions = new Mock<IDirectSessionLocator>(MockBehavior.Loose);
            directSessions.Setup(s => s.GetAsync(It.IsAny<Percolator.Identity.PeerId>(), It.IsAny<uint>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((Percolator.Network.DirectSessionId?)new Percolator.Network.DirectSessionId(Guid.NewGuid()));

            var secure = new Mock<ISecureMessagingService>(MockBehavior.Loose);
            secure.Setup(s => s.EncryptAsync(It.IsAny<Percolator.Cryptography.SessionId>(), It.IsAny<Plaintext>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(SessionRatchetMessage.FromBytes(new byte[] { 1, 2, 3 }));

            var transport = new Mock<IMessageTransportService>(MockBehavior.Loose);
            transport.Setup(t => t.SendMessageAsync(
                    It.IsAny<Percolator.Identity.PeerId>(),
                    It.IsAny<Percolator.Network.DirectSessionId>(),
                    It.IsAny<SessionRatchetMessage>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(new SendMessageResponse { OriginalResponse = new Percolator.Contracts.DeliverOpaqueMessageResponse() });
            var mediator = new Mock<IMediator>(MockBehavior.Loose);

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

            var result = await sut.Handle(new ApprovePendingSessionCommand(pendingId, identity.SelfIdentityId), CancellationToken.None);
            result.Should().BeOfType<ApprovePendingSessionResult.Accepted>();

            // Verify state: pending session should be deleted
            pendingRepo.Setup(r => r.GetAsync(pendingId, It.IsAny<CryptoSelfId>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((PendingSession?)null);
            var deleted = await pendingRepo.Object.GetAsync(pendingId, new CryptoSelfId(1), CancellationToken.None);
            deleted.Should().BeNull();
        }
    }
}

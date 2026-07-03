using System.Net;
using System.Security.Cryptography;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using MediatR;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Percolator.Application.Identity;
using Percolator.Application.Network;
using Percolator.Application.ReverseSignal;
using Percolator.Application.Services;
using Percolator.ApplicationTests.Services;
using Percolator.Contracts;
using Percolator.Cryptography;
using Percolator.Cryptography.Primitives;
using Percolator.Identity;
using Percolator.Identity.Model;
using Percolator.Network;
using NetworkPeerId = Percolator.Network.PeerId;

namespace Percolator.ApplicationTests.Network;

[TestFixture]
public sealed class ApprovePendingSessionCommandTests
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
    public async Task Handle_WhenAcceptingDirectInvite_UpsertsDirectSessionMapping()
    {
        // Arrange
        var logger = NullLogger<ApprovePendingSessionHandler>.Instance;
        var activeAccessor = new ActiveAccessorStub { IsActive = true };
        var active = new ActiveIdentityContext();
        var selfIdentityId = new SelfId(7);
        var identity = new IdentityRecord(selfIdentityId, new PublicIdentityId(Guid.NewGuid()), new Percolator.Identity.DeviceId(1), "self");
        var keys = new X3dhKeys(
            ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256),
            ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256));
        active.SetActiveIdentity(identity, keys);

        var clock = new TestClock();
        var correlation = new RequestCorrelationId(Guid.NewGuid());
        var remotePeerId = (uint)Random.Shared.Next(1, 1000000);
        var pendingId = PendingSessionId.NewId();

        using var inviterEcdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var inviterSpki = inviterEcdh.ExportSubjectPublicKeyInfo();

        var payload = new InviteHandshakeRequestPayload
        {
            Version = 1,
            InviterHost = "example.com",
            InviterPort = 7777,
            ExpiresAtUtc = Timestamp.FromDateTimeOffset(clock.UtcNow.AddMinutes(10)),
            RequestCorrelationId = correlation.ToString(),
            InviterPreKey = new InviteHandshakePreKeyBundle
            {
                Version = 1,
                InviterSignedPreKey = ByteString.CopyFrom(inviterEcdh.ExportSubjectPublicKeyInfo()),
                PreKeySignature = ByteString.CopyFrom(new byte[64])
            }
        };

        var pending = BuildPending(
            pendingId,
            new Percolator.Cryptography.Primitives.PeerId(remotePeerId),
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

        var sessions = new Mock<ISessionRepository>(MockBehavior.Loose);

        var profileRepo = new Mock<IPeerRoutingProfileRepository>(MockBehavior.Loose);
        var delivery = new Mock<IInviteHandshakeResponseDeliveryService>(MockBehavior.Strict);
        var deliveryResult = new InviteHandshakeResponseDeliveryResult(true, "direct");
        delivery.Setup(d => d.DeliverAsync(
                It.IsAny<NetworkPeerId>(),
                It.IsAny<DnsEndPoint>(),
                It.IsAny<InviteHandshakeResponse>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(deliveryResult);

        var directSessionLocator = new Mock<IDirectSessionLocator>(MockBehavior.Loose);
        var directSessionMappingWriter = new Mock<IDirectSessionMappingWriter>(MockBehavior.Strict);
        var secureMessaging = new Mock<ISecureMessagingService>(MockBehavior.Loose);
        var transport = new Mock<IMessageTransportService>(MockBehavior.Loose);
        var mediator = new Mock<IMediator>(MockBehavior.Loose);

        // Setup WriteMappingAsync to capture the session ID for verification
        DirectSessionId? writtenSessionId = null;
        directSessionMappingWriter
            .Setup(w => w.WriteMappingAsync(It.IsAny<NetworkPeerId>(), It.IsAny<DirectSessionId>(), It.IsAny<SelfId>(), It.IsAny<CancellationToken>()))
            .Callback<NetworkPeerId, DirectSessionId, SelfId, CancellationToken>((_, sid, _, _) =>
            {
                writtenSessionId = sid;
            })
            .Returns(Task.CompletedTask);

        // Setup locator to return the written session ID for verification
        directSessionLocator
            .Setup(l => l.GetAsync(It.IsAny<Percolator.Identity.PeerId>(), It.IsAny<uint>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => writtenSessionId);

        // Act
        var handler = new ApprovePendingSessionHandler(
            logger,
            activeAccessor,
            active,
            pendingRepo.Object,
            clock,
            callbackValidator.Object,
            sessionCrypto.Object,
            planner.Object,
            sessions.Object,
            profileRepo.Object,
            delivery.Object,
            directSessionLocator.Object,
            directSessionMappingWriter.Object,
            secureMessaging.Object,
            transport.Object,
            mediator.Object);

        var result = await handler.Handle(
            new ApprovePendingSessionCommand(pendingId, selfIdentityId),
            CancellationToken.None);

        // Assert
        result.Should().BeOfType<ApprovePendingSessionResult.Accepted>();

        // Verify state change: the mapping should be retrievable via the locator
        var locatedSessionId = await directSessionLocator.Object.GetAsync(
            new Percolator.Identity.PeerId(remotePeerId),
            selfIdentityId.Value,
            CancellationToken.None);
        locatedSessionId.Should().NotBeNull();
    }
}

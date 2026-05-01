using System.Net;
using System.Security.Cryptography;
using FluentAssertions;
using Google.Protobuf;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Percolator.Application.Cli;
using Percolator.Application.Identity;
using Percolator.Application.Network;
using Percolator.Application.Services;
using Percolator.Contracts;
using Percolator.Cryptography;
using Percolator.Identity;
using Percolator.Network;

namespace Percolator.ApplicationTests.Cli;

[TestFixture]
public sealed class RequestPreKeyBundleByPkhHandlerTests
{
    private ActiveIdentityContext _activeIdentity = null!;

    private Mock<IDirectSessionLocator> _directSessionLocator = null!;
    private Mock<ISecureMessagingService> _secureMessaging = null!;
    private Mock<IMessageTransportService> _transport = null!;
    private Mock<IPeerPublicSigningKeyStore> _peerKeyStore = null!;
    private Mock<ISessionCrypto> _sessionCrypto = null!;
    private Mock<IGrpcSessionService> _grpcSessions = null!;
    private Mock<Percolator.Cryptography.ISessionRepository> _sessions = null!;
    private Mock<IDirectSessionRepository> _directSessions = null!;
    private Mock<IPeerRoutingProfileRepository> _routingProfiles = null!;
    private Mock<IProfileRoutePlanner> _routePlanner = null!;
    private Mock<IClock> _clock = null!;

    [SetUp]
    public void SetUp()
    {
        _activeIdentity = new ActiveIdentityContext();

        _directSessionLocator = new Mock<IDirectSessionLocator>(MockBehavior.Loose);
        _secureMessaging = new Mock<ISecureMessagingService>(MockBehavior.Loose);
        _transport = new Mock<IMessageTransportService>(MockBehavior.Loose);
        _peerKeyStore = new Mock<IPeerPublicSigningKeyStore>(MockBehavior.Loose);
        _sessionCrypto = new Mock<ISessionCrypto>(MockBehavior.Loose);
        _grpcSessions = new Mock<IGrpcSessionService>(MockBehavior.Loose);
        _sessions = new Mock<Percolator.Cryptography.ISessionRepository>(MockBehavior.Loose);
        _directSessions = new Mock<IDirectSessionRepository>(MockBehavior.Loose);
        _routingProfiles = new Mock<IPeerRoutingProfileRepository>(MockBehavior.Loose);
        _routePlanner = new Mock<IProfileRoutePlanner>(MockBehavior.Loose);
        _clock = new Mock<IClock>(MockBehavior.Loose);

        _clock.SetupGet(c => c.UtcNow).Returns(new DateTimeOffset(2026, 03, 13, 12, 0, 0, TimeSpan.Zero));
    }

    [Test]
    public async Task Handle_PersistsSession_And_UpsertsDirectSession_And_UpdatesRoutingProfile()
    {
        // Arrange
        var selfIdentityId = 1;
        var selfPeerGuid = Guid.NewGuid();
        _activeIdentity.Identity = new Percolator.Identity.Model.IdentityRecord(selfPeerGuid, "self")
        {
            SelfIdentityId = new SelfId(selfIdentityId)
        };
        _activeIdentity.Keys = new X3dhKeys(
            ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256),
            ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256));

        var remotePeerId = new Percolator.Identity.PeerId(Guid.NewGuid());
        var remoteIdentitySpki = new byte[80];
        var expectedPkh = SHA256.HashData(remoteIdentitySpki);
        var identityPublicKeyHash = IdentityPublicKeyHash.FromBytes(expectedPkh);

        _peerKeyStore
            .Setup(s => s.GetPeerIdByPublicKeyHashAsync(
                It.Is<IdentityPublicKeyHash>(h => h.Equals(identityPublicKeyHash)),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(remotePeerId);

        var hostDirectSessionId = new DirectSessionId(Guid.NewGuid());
        _directSessionLocator
            .Setup(l => l.GetAsync(remotePeerId, selfIdentityId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(hostDirectSessionId);

        _secureMessaging
            .Setup(s => s.EncryptAsync(It.IsAny<SessionId>(), It.IsAny<Plaintext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(SessionRatchetMessage.FromBytes(new byte[] { 1, 2, 3 }));

        var signedPreKeyId = Guid.NewGuid();
        var oneTimePreKeyId = Guid.NewGuid();

        var bundle = new GetPreKeyBundleResponse.Types.PreKeyBundle
        {
            Version = 1,
            IdentityKey = ByteString.CopyFrom(remoteIdentitySpki),
            SignedPreKeyId = ByteString.CopyFrom(signedPreKeyId.ToByteArray()),
            SignedPreKey = ByteString.CopyFrom(new byte[64]),
            PreKeySignature = ByteString.CopyFrom(new byte[64])
        };

        bundle.OneTimeKeys.Add(new GetPreKeyBundleResponse.Types.OneTimeKey
        {
            Version = 1,
            OneTimeKeyId = ByteString.CopyFrom(oneTimePreKeyId.ToByteArray()),
            KeyBytes = ByteString.CopyFrom(new byte[] { 3, 3, 3 })
        });

        var internalResp = new InternalEnvelope
        {
            GetPreKeyBundleResponse = new GetPreKeyBundleResponse
            {
                Version = 1,
                PreKeyBundle = bundle
            }
        };

        var respBytes = internalResp.ToByteArray();

        _transport
            .Setup(t => t.SendMessageAsync(remotePeerId, hostDirectSessionId, It.IsAny<SessionRatchetMessage>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SendMessageResponse
            {
                OriginalResponse = new DeliverOpaqueMessageResponse
                {
                    Version = 1,
                    ResponsePayload = new DeliverOpaqueMessageResponse.Types.Payload
                    {
                        Version = 1,
                        ResponsePayload = ByteString.CopyFrom(respBytes)
                    }
                }
            });

        _secureMessaging
            .Setup(s => s.DecryptInboundAsync(selfIdentityId, It.IsAny<SessionRatchetMessage>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((new SessionId(hostDirectSessionId.Value), Plaintext.FromBytes(respBytes)));

        var endpoint = new DnsEndPoint("example.com", 7777);
        var profile = new PeerRoutingProfile();
        profile.BindIdentity(new Percolator.Network.PeerId(remotePeerId.Value));
        profile.AddGrpcEndPoint(new GrpcEndPoint(endpoint, _clock.Object.UtcNow), _clock.Object.UtcNow);
        _routingProfiles
            .Setup(r => r.GetByIdAsync(new Percolator.Network.PeerId(remotePeerId.Value), It.IsAny<CancellationToken>()))
            .ReturnsAsync(profile);

        _routePlanner
            .Setup(p => p.SelectRoute(profile))
            .Returns(new RouteSelection(new GrpcEndPoint(endpoint, _clock.Object.UtcNow), null));

        _sessionCrypto
            .Setup(c => c.VerifySignature(It.IsAny<RatchetIdentityKey>(), It.IsAny<PreKey>(), It.IsAny<Percolator.Cryptography.Signature>()))
            .Returns(true);

        var sharedSecret = SharedSecret.FromBytes(new byte[32]);
        var eph = RatchetEphemeralKey.FromBytes(new byte[64]);
        _sessionCrypto
            .Setup(c => c.X3DH_Initiate(It.IsAny<PrivatePreKey>(), It.IsAny<Percolator.Cryptography.PreKeyBundle>()))
            .Returns((sharedSecret, eph));

        var newSessionGuid = Guid.NewGuid();
        var establishPayload = new EstablishSessionResponse.Types.Response.Types.ResponsePayload
        {
            Version = 1,
            SessionId = newSessionGuid.ToString(),
            EphemeralKey = ByteString.CopyFrom(new byte[] { 7 })
        };
        var establishResp = new EstablishSessionResponse
        {
            Version = 1,
            Response = new EstablishSessionResponse.Types.Response
            {
                Version = 1,
                ResponsePayload = ByteString.CopyFrom(establishPayload.ToByteArray())
            }
        };

        _grpcSessions
            .Setup(g => g.EstablishSessionAsync(endpoint, It.IsAny<EstablishSessionRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(establishResp);

        _sessions
            .Setup(s => s.AddAsync(It.IsAny<SecureSession>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _directSessions
            .Setup(s => s.UpsertAsync(It.IsAny<Percolator.Network.PeerId>(), It.IsAny<DirectSessionId>(), It.IsAny<int>()))
            .Returns(Task.CompletedTask);
        _routingProfiles
            .Setup(r => r.UpsertAsync(It.IsAny<PeerRoutingProfile>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var sut = new RequestPreKeyBundleByPkhHandler(
            NullLogger<RequestPreKeyBundleByPkhHandler>.Instance,
            _directSessionLocator.Object,
            _secureMessaging.Object,
            _transport.Object,
            _activeIdentity,
            _peerKeyStore.Object,
            _sessionCrypto.Object,
            _grpcSessions.Object,
            _sessions.Object,
            _directSessions.Object,
            _routingProfiles.Object,
            _routePlanner.Object,
            _clock.Object);

        var cmd = new RequestPreKeyBundleByPkhCommand(expectedPkh);

        // Act
        Func<Task> act = () => sut.Handle(cmd, CancellationToken.None);

        // Assert
        await act.Should().NotThrowAsync();

        _sessions.Verify(s => s.AddAsync(
            It.Is<SecureSession>(ss => ss.Id.Value == newSessionGuid),
            It.IsAny<CancellationToken>()), Times.Once);

        _directSessions.Verify(s => s.UpsertAsync(
            It.Is<Percolator.Network.PeerId>(p => p.Value == remotePeerId.Value),
            It.Is<DirectSessionId>(ds => ds.Value == newSessionGuid),
            It.Is<int>(sid => sid == selfIdentityId)), Times.Once);

        _routingProfiles.Verify(r => r.UpsertAsync(
            It.Is<PeerRoutingProfile>(p => p.Id != null && p.Id.Value == remotePeerId.Value),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Test]
    public async Task Handle_Throws_WhenPeerNotFoundByPkh()
    {
        // Arrange
        var sut = new RequestPreKeyBundleByPkhHandler(
            NullLogger<RequestPreKeyBundleByPkhHandler>.Instance,
            _directSessionLocator.Object,
            _secureMessaging.Object,
            _transport.Object,
            _activeIdentity,
            _peerKeyStore.Object,
            _sessionCrypto.Object,
            _grpcSessions.Object,
            _sessions.Object,
            _directSessions.Object,
            _routingProfiles.Object,
            _routePlanner.Object,
            _clock.Object);

        var cmd = new RequestPreKeyBundleByPkhCommand(new byte[32].Select((_, i) => i < 3 ? (byte)(i + 1) : (byte)0).ToArray());

        // Act
        Func<Task> act = () => sut.Handle(cmd, CancellationToken.None);

        // Assert
        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Test]
    public async Task Handle_Throws_WhenDirectSessionToHostIsMissing()
    {
        // Arrange
        var selfIdentityId = 1;
        _activeIdentity.Identity = new Percolator.Identity.Model.IdentityRecord(Guid.NewGuid(), "self")
        {
            SelfIdentityId = new SelfId(selfIdentityId)
        };

        var remoteIdentitySpki = new byte[80];
        var expectedPkh = SHA256.HashData(remoteIdentitySpki);
        var identityPublicKeyHash = IdentityPublicKeyHash.FromBytes(expectedPkh);
        var remotePeerId = new Percolator.Identity.PeerId(Guid.NewGuid());

        _peerKeyStore
            .Setup(s => s.GetPeerIdByPublicKeyHashAsync(
                It.Is<IdentityPublicKeyHash>(h => h.Equals(identityPublicKeyHash)),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(remotePeerId);

        _directSessionLocator
            .Setup(l => l.GetAsync(remotePeerId, selfIdentityId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((DirectSessionId?)null);

        var sut = new RequestPreKeyBundleByPkhHandler(
            NullLogger<RequestPreKeyBundleByPkhHandler>.Instance,
            _directSessionLocator.Object,
            _secureMessaging.Object,
            _transport.Object,
            _activeIdentity,
            _peerKeyStore.Object,
            _sessionCrypto.Object,
            _grpcSessions.Object,
            _sessions.Object,
            _directSessions.Object,
            _routingProfiles.Object,
            _routePlanner.Object,
            _clock.Object);

        var cmd = new RequestPreKeyBundleByPkhCommand(expectedPkh);

        // Act
        Func<Task> act = () => sut.Handle(cmd, CancellationToken.None);

        // Assert
        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Test]
    public async Task Handle_Throws_WhenReturnedBundleIdentityDoesNotMatchRequestedPkh()
    {
        // Arrange
        var selfIdentityId = 1;
        _activeIdentity.Identity = new Percolator.Identity.Model.IdentityRecord(Guid.NewGuid(), "self")
        {
            SelfIdentityId = new SelfId(selfIdentityId)
        };
        _activeIdentity.Keys = new X3dhKeys(
            ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256),
            ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256));

        var expectedIdentitySpki = new byte[] { 1, 2, 3, 4 };
        var expectedPkh = SHA256.HashData(expectedIdentitySpki);
        var identityPublicKeyHash = IdentityPublicKeyHash.FromBytes(expectedPkh);

        var remotePeerId = new Percolator.Identity.PeerId(Guid.NewGuid());
        _peerKeyStore
            .Setup(s => s.GetPeerIdByPublicKeyHashAsync(
                It.Is<IdentityPublicKeyHash>(h => h.Equals(identityPublicKeyHash)),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(remotePeerId);

        var hostDirectSessionId = new DirectSessionId(Guid.NewGuid());
        _directSessionLocator
            .Setup(l => l.GetAsync(remotePeerId, selfIdentityId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(hostDirectSessionId);

        _secureMessaging
            .Setup(s => s.EncryptAsync(It.IsAny<SessionId>(), It.IsAny<Plaintext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(SessionRatchetMessage.FromBytes(new byte[] { 1 }));

        var endpoint = new DnsEndPoint("example.com", 7777);
        var profile = new PeerRoutingProfile();
        profile.BindIdentity(new Percolator.Network.PeerId(remotePeerId.Value));
        profile.AddGrpcEndPoint(new GrpcEndPoint(endpoint, _clock.Object.UtcNow), _clock.Object.UtcNow);
        _routingProfiles
            .Setup(r => r.GetByIdAsync(new Percolator.Network.PeerId(remotePeerId.Value), It.IsAny<CancellationToken>()))
            .ReturnsAsync(profile);
        _routePlanner
            .Setup(p => p.SelectRoute(profile))
            .Returns(new RouteSelection(new GrpcEndPoint(endpoint, _clock.Object.UtcNow), null));

        // Bundle has a different identity key than the requested PKH
        var differentIdentitySpki = new byte[] { 9, 9, 9 };
        var signedPreKeyId = Guid.NewGuid();
        var bundle = new GetPreKeyBundleResponse.Types.PreKeyBundle
        {
            Version = 1,
            IdentityKey = ByteString.CopyFrom(differentIdentitySpki),
            SignedPreKeyId = ByteString.CopyFrom(signedPreKeyId.ToByteArray()),
            SignedPreKey = ByteString.CopyFrom(new byte[] { 1 }),
            PreKeySignature = ByteString.CopyFrom(new byte[] { 2 })
        };

        var internalResp = new InternalEnvelope
        {
            GetPreKeyBundleResponse = new GetPreKeyBundleResponse
            {
                Version = 1,
                PreKeyBundle = bundle
            }
        };
        var respBytes = internalResp.ToByteArray();

        _transport
            .Setup(t => t.SendMessageAsync(remotePeerId, hostDirectSessionId, It.IsAny<SessionRatchetMessage>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SendMessageResponse
            {
                OriginalResponse = new DeliverOpaqueMessageResponse
                {
                    Version = 1,
                    ResponsePayload = new DeliverOpaqueMessageResponse.Types.Payload
                    {
                        Version = 1,
                        ResponsePayload = ByteString.CopyFrom(respBytes)
                    }
                }
            });

        _secureMessaging
            .Setup(s => s.DecryptInboundAsync(selfIdentityId, It.IsAny<SessionRatchetMessage>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((new SessionId(hostDirectSessionId.Value), Plaintext.FromBytes(respBytes)));

        _sessionCrypto
            .Setup(c => c.VerifySignature(It.IsAny<RatchetIdentityKey>(), It.IsAny<PreKey>(), It.IsAny<Percolator.Cryptography.Signature>()))
            .Returns(true);

        var sut = new RequestPreKeyBundleByPkhHandler(
            NullLogger<RequestPreKeyBundleByPkhHandler>.Instance,
            _directSessionLocator.Object,
            _secureMessaging.Object,
            _transport.Object,
            _activeIdentity,
            _peerKeyStore.Object,
            _sessionCrypto.Object,
            _grpcSessions.Object,
            _sessions.Object,
            _directSessions.Object,
            _routingProfiles.Object,
            _routePlanner.Object,
            _clock.Object);

        var cmd = new RequestPreKeyBundleByPkhCommand(expectedPkh);

        // Act
        Func<Task> act = () => sut.Handle(cmd, CancellationToken.None);

        // Assert
        await act.Should().ThrowAsync<InvalidOperationException>();
    }
}

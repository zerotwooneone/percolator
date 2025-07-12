using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using Google.Protobuf;
using Grpc.Core;
using Grpc.Core.Testing;
using Microsoft.Extensions.Logging;
using Moq;
using Moq.Protected;
using NUnit.Framework;
using Percolator.Application.Identity;
using Percolator.Application.KeyExchange;
using Percolator.Application.Network;
using Percolator.Application.Sessions;
using Percolator.Chat;
using Percolator.Contracts;
using Percolator.Cryptography;
using Percolator.Identity;
using Percolator.Identity.Model;
using Percolator.Infrastructure.Sessions;
using Percolator.Network;
using NetworkPeerId = Percolator.Network.PeerId;
using Percolator.Sessions;
using ChatConversation = Percolator.Chat.Conversation;
using ChatConversationId = Percolator.Chat.ValueObjects.ConversationId;
using ChatParticipantId = Percolator.Chat.ValueObjects.ParticipantId;
using ContractsPreKeyBundle = Percolator.Contracts.PreKeyBundle;
using IdentityPeerId = Percolator.Identity.PeerId;
using SessionConversationId = Percolator.Sessions.ConversationId;
using SessionPeerId = Percolator.Sessions.PeerId;
using SessionSharedSecret = Percolator.Sessions.SharedSecret;
using TransportService = Percolator.Contracts.TransportService;

namespace Percolator.ApplicationTests.Sessions;

[TestFixture]
public class ConversationServiceTests
{
    private Mock<IPeerRepository> _mockPeerRepository = null!;
    private Mock<ITlsCertificateService> _mockTlsCertificateService = null!;
    private Mock<IX3DHOrchestrator> _mockX3dhOrchestrator = null!;
    private Mock<IDirectSessionManager> _mockSessionManager = null!;
    private Mock<IConversationRepository> _mockConversationRepository = null!;
    private Mock<IPeerConnectionRepository> _mockPeerConnectionRepository = null!;
    private Mock<IOneTimeKeyProvider> _mockOneTimeKeyProvider = null!;
    private Mock<IHttpClientFactory> _mockHttpClientFactory = null!;
    private Mock<IPeerTrustManager> _mockPeerTrustManager = null!;
    private Mock<ILogger<ConversationService>> _mockLogger = null!;
    private ConversationService _service = null!;
    private ActiveIdentityContext _activeIdentityContext = null!;

    [SetUp]
    public void Setup()
    {
        _mockPeerRepository = new Mock<IPeerRepository>();
        _mockTlsCertificateService = new Mock<ITlsCertificateService>();
        _mockX3dhOrchestrator = new Mock<IX3DHOrchestrator>();
        _mockSessionManager = new Mock<IDirectSessionManager>();
        _mockOneTimeKeyProvider = new Mock<IOneTimeKeyProvider>();
        _mockConversationRepository = new Mock<IConversationRepository>();
        _mockPeerConnectionRepository = new Mock<IPeerConnectionRepository>();
        _mockHttpClientFactory = new Mock<IHttpClientFactory>();
        _mockPeerTrustManager = new Mock<IPeerTrustManager>();
        _mockLogger = new Mock<ILogger<ConversationService>>();
        _activeIdentityContext = new ActiveIdentityContext
        {
            Identity = new IdentityRecord(Guid.NewGuid(), "Test Identity"),
            Keys = new X3dhKeys(
                ECDsa.Create(ECCurve.NamedCurves.nistP256),
                ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256),
                ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256)
            )
        };

        _service = new ConversationService(
            _mockLogger.Object,
            _mockX3dhOrchestrator.Object,
            _mockSessionManager.Object,
            _mockConversationRepository.Object,
            _mockPeerRepository.Object,
            _mockPeerConnectionRepository.Object,
            _mockTlsCertificateService.Object,
            _mockOneTimeKeyProvider.Object,
            _activeIdentityContext,
            _mockHttpClientFactory.Object,
            _mockPeerTrustManager.Object
        );
    }

    [Test]
    public async Task CreateDirectConversationAsync_WhenPeerExists_EstablishesSessionAndCreatesConversation()
    {
        // Arrange
        var endpoint = new DnsEndPoint("localhost", 5001);
        var peerName = "test-peer";
        var peer = new Peer(new IdentityPeerId(Guid.NewGuid()), peerName);
        var responseMessage = new EstablishSessionResponse
        {
            ResponderBundle = new ContractsPreKeyBundle
            {
                IdentitySigningKey = ByteString.CopyFrom(new byte[32]),
                IdentityAgreementKey = ByteString.CopyFrom(new byte[32]),
                SignedPreKey = ByteString.CopyFrom(new byte[32]),
                PreKeySignature = ByteString.CopyFrom(new byte[64])
            }
        };

        _mockPeerRepository.Setup(r => r.GetByNameAsync(peerName)).ReturnsAsync(peer);
        _mockConversationRepository.Setup(r => r.AddAsync(It.IsAny<ChatConversation>())).Returns(Task.CompletedTask);
        _mockSessionManager.Setup(m => m.EstablishSessionAsInitiatorAsync(It.IsAny<SessionConversationId>(), It.IsAny<SessionPeerId>(), It.IsAny<SessionIdentityKey>(), It.IsAny<SessionRatchetKey>(), It.IsAny<SessionSharedSecret>()))
            .Returns(Task.CompletedTask);
        _mockPeerConnectionRepository.Setup(r => r.UpdateDirectMessagePublicKeyAsync(It.IsAny<NetworkPeerId>(), It.IsAny<DirectMessagePublicKey>()))
            .Returns(Task.CompletedTask);

        var mockHttpHandler = new Mock<HttpMessageHandler>();
        mockHttpHandler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync", ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>()) 
            .ReturnsAsync(CreateGrpcResponse(responseMessage));

        var httpClient = new HttpClient(mockHttpHandler.Object);
        _mockHttpClientFactory.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(httpClient);

        // Act
        var conversationId = await _service.CreateDirectConversationAsync(endpoint, peerName);

        // Assert
        Assert.That(conversationId, Is.Not.EqualTo(default(ChatConversationId)));
        _mockConversationRepository.Verify(r => r.AddAsync(It.Is<ChatConversation>(c => c.Name == peerName)), Times.Once);
        _mockSessionManager.Verify(m => m.EstablishSessionAsInitiatorAsync(It.IsAny<SessionConversationId>(), It.IsAny<SessionPeerId>(), It.IsAny<SessionIdentityKey>(), It.IsAny<SessionRatchetKey>(), It.IsAny<SessionSharedSecret>()), Times.Once);
    }

    private static HttpResponseMessage CreateGrpcResponse<T>(T message)
        where T : IMessage
    {
        var stream = new MemoryStream();
        // Write the compression flag (0 for uncompressed)
        stream.WriteByte(0);
        // Write the 4-byte message length
        var length = message.CalculateSize();
        var lengthBytes = BitConverter.GetBytes(length);
        if (BitConverter.IsLittleEndian)
        {
            Array.Reverse(lengthBytes);
        }
        stream.Write(lengthBytes, 0, 4);
        // Write the message
        message.WriteTo(stream);
        stream.Position = 0;

        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Version = new Version(2, 0),
            Content = new StreamContent(stream)
        };
        response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/grpc");
        return response;
    }
}
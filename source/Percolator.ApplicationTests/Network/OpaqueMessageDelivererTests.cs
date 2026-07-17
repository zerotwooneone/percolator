using FluentAssertions;
using Google.Protobuf;
using MediatR;
using Microsoft.Extensions.Logging;
using Moq;
using Percolator.Application.Network;
using Percolator.Application.Services;
using Percolator.Contracts;
using Percolator.Cryptography;
using Percolator.Identity;
using Percolator.Network;

namespace Percolator.ApplicationTests.Network;

[TestFixture]
public class OpaqueMessageDelivererTests
{
    private Mock<ILogger<OpaqueMessageDeliverer>> _loggerMock;
    private Mock<IMediator> _mediatorMock;
    private Mock<IDirectSessionRepository> _directSessionRepositoryMock;
    private Mock<IRatchetKeyIndex> _ratchetLookupMock;
    private Mock<ISecureMessagingService> _secureMessagingMock;
    private OpaqueMessageDeliverer _deliverer;

    [SetUp]
    public void Setup()
    {
        _loggerMock = new Mock<ILogger<OpaqueMessageDeliverer>>();
        _mediatorMock = new Mock<IMediator>();
        _directSessionRepositoryMock = new Mock<IDirectSessionRepository>();
        _ratchetLookupMock = new Mock<IRatchetKeyIndex>();
        _secureMessagingMock = new Mock<ISecureMessagingService>();

        _deliverer = new OpaqueMessageDeliverer(
            _loggerMock.Object,
            _mediatorMock.Object,
            _directSessionRepositoryMock.Object,
            _ratchetLookupMock.Object,
            _secureMessagingMock.Object);
    }

    [Test]
    public async Task DeliverAsync_Success_ReturnsTrue()
    {
        // Arrange
        var sessionId = Guid.NewGuid();
        var remotePeerId = 12345u;
        var selfIdentityId = Guid.NewGuid();
        var headerKeyBytes = new byte[64];
        var plaintextBytes = new InternalEnvelope
        {
            ChatEnvelope = new ChatEnvelope { TextMessage = "test" },
            SourceDeviceId = 1
        }.ToByteArray();
        var plain = Plaintext.FromBytes(plaintextBytes);
        var payloadBytes = BuildRatchetPayload(headerKeyBytes, plain.ToArray(), selfIdentityId);

        _secureMessagingMock.Setup(s => s.DecryptInboundAsync(It.IsAny<CryptoSelfId>(), It.IsAny<SessionRatchetMessage>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((new SessionId(sessionId), plain));

        _directSessionRepositoryMock.Setup(r => r.GetBySessionIdAsync(It.IsAny<DirectSessionId>(), It.IsAny<NetworkSelfId>()))
            .ReturnsAsync(new DirectSession(new NetworkPeerId(remotePeerId), new DirectSessionId(sessionId)));

        _ratchetLookupMock.Setup(l => l.UpsertAsync(It.IsAny<CryptoSelfId>(), It.IsAny<SessionId>(), It.IsAny<RatchetEphemeralKey>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        _mediatorMock.Setup(m => m.Send(It.IsAny<ProcessInternalEnvelopeCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((InternalEnvelope?)null);

        // Act
        var result = await _deliverer.DeliverAsync(payloadBytes, CancellationToken.None);

        // Assert
        result.Should().BeTrue();
        _mediatorMock.Verify(m => m.Send(It.IsAny<ProcessInternalEnvelopeCommand>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Test]
    public async Task DeliverAsync_DecryptionFailure_ReturnsFalse()
    {
        // Arrange
        var payloadBytes = new byte[100];

        _secureMessagingMock.Setup(s => s.DecryptInboundAsync(It.IsAny<CryptoSelfId>(), It.IsAny<SessionRatchetMessage>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((SessionId?, Plaintext?)null);

        // Act
        var result = await _deliverer.DeliverAsync(payloadBytes, CancellationToken.None);

        // Assert
        result.Should().BeFalse();
        _mediatorMock.Verify(m => m.Send(It.IsAny<ProcessInternalEnvelopeCommand>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Test]
    public async Task DeliverAsync_NullPlaintext_ReturnsFalse()
    {
        // Arrange
        var sessionId = Guid.NewGuid();
        var remotePeerId = 12345u;
        var selfIdentityId = Guid.NewGuid();
        var headerKeyBytes = new byte[64];
        var payloadBytes = BuildRatchetPayload(headerKeyBytes, new byte[0], selfIdentityId);

        _secureMessagingMock.Setup(s => s.DecryptInboundAsync(It.IsAny<CryptoSelfId>(), It.IsAny<SessionRatchetMessage>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((new SessionId(sessionId), (Plaintext?)null));

        // Act
        var result = await _deliverer.DeliverAsync(payloadBytes, CancellationToken.None);

        // Assert
        result.Should().BeFalse();
        _mediatorMock.Verify(m => m.Send(It.IsAny<ProcessInternalEnvelopeCommand>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Test]
    public async Task DeliverAsync_UnallowedEnvelopeCase_ReturnsFalse()
    {
        // Arrange
        var sessionId = Guid.NewGuid();
        var remotePeerId = 12345u;
        var selfIdentityId = Guid.NewGuid();
        var headerKeyBytes = new byte[64];
        var plaintextBytes = new InternalEnvelope
        {
            SourceDeviceId = 1
        }.ToByteArray();
        var plain = Plaintext.FromBytes(plaintextBytes);
        var payloadBytes = BuildRatchetPayload(headerKeyBytes, plain.ToArray(), selfIdentityId);

        _secureMessagingMock.Setup(s => s.DecryptInboundAsync(It.IsAny<CryptoSelfId>(), It.IsAny<SessionRatchetMessage>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((new SessionId(sessionId), plain));

        _directSessionRepositoryMock.Setup(r => r.GetBySessionIdAsync(It.IsAny<DirectSessionId>(), It.IsAny<NetworkSelfId>()))
            .ReturnsAsync(new DirectSession(new NetworkPeerId(remotePeerId), new DirectSessionId(sessionId)));

        _ratchetLookupMock.Setup(l => l.UpsertAsync(It.IsAny<CryptoSelfId>(), It.IsAny<SessionId>(), It.IsAny<RatchetEphemeralKey>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        // Act
        var result = await _deliverer.DeliverAsync(payloadBytes, CancellationToken.None);

        // Assert
        result.Should().BeFalse();
        _mediatorMock.Verify(m => m.Send(It.IsAny<ProcessInternalEnvelopeCommand>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Test]
    public async Task DeliverAsync_MissingSourceDeviceId_ReturnsFalse()
    {
        // Arrange
        var sessionId = Guid.NewGuid();
        var remotePeerId = 12345u;
        var selfIdentityId = Guid.NewGuid();
        var headerKeyBytes = new byte[64];
        var plaintextBytes = new InternalEnvelope
        {
            ChatEnvelope = new ChatEnvelope { TextMessage = "test" }
        }.ToByteArray();
        var plain = Plaintext.FromBytes(plaintextBytes);
        var payloadBytes = BuildRatchetPayload(headerKeyBytes, plain.ToArray(), selfIdentityId);

        _secureMessagingMock.Setup(s => s.DecryptInboundAsync(It.IsAny<CryptoSelfId>(), It.IsAny<SessionRatchetMessage>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((new SessionId(sessionId), plain));

        _directSessionRepositoryMock.Setup(r => r.GetBySessionIdAsync(It.IsAny<DirectSessionId>(), It.IsAny<NetworkSelfId>()))
            .ReturnsAsync(new DirectSession(new NetworkPeerId(remotePeerId), new DirectSessionId(sessionId)));

        _ratchetLookupMock.Setup(l => l.UpsertAsync(It.IsAny<CryptoSelfId>(), It.IsAny<SessionId>(), It.IsAny<RatchetEphemeralKey>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        // Act
        var result = await _deliverer.DeliverAsync(payloadBytes, CancellationToken.None);

        // Assert
        result.Should().BeFalse();
        _mediatorMock.Verify(m => m.Send(It.IsAny<ProcessInternalEnvelopeCommand>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    private byte[] BuildRatchetPayload(byte[] headerKey, byte[] plaintext, Guid selfIdentityId)
    {
        // This is a simplified ratchet payload builder for testing
        // In reality, this would use the actual SessionRatchetMessage serialization
        var combined = new byte[headerKey.Length + plaintext.Length + 16];
        Buffer.BlockCopy(headerKey, 0, combined, 0, headerKey.Length);
        Buffer.BlockCopy(plaintext, 0, combined, headerKey.Length, plaintext.Length);
        Buffer.BlockCopy(selfIdentityId.ToByteArray(), 0, combined, headerKey.Length + plaintext.Length, 16);
        return combined;
    }
}

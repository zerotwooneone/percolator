using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Google.Protobuf;
using MediatR;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Percolator.Application.Apps.Chat;
using Percolator.Application.Network;
using Percolator.Contracts;
using Percolator.Cryptography;
using Percolator.Identity;
using Percolator.Network;

// Alias to disambiguate between the two PeerId types
using PeerId = Percolator.Identity.PeerId;

namespace Percolator.ApplicationTests.Apps.Chat;

public class DispatchTextMessageHandlerTests
{
    private readonly Mock<IMediator> _mediatorMock;
    private readonly Mock<IRemoteEnvelopeSender> _senderMock;
    private readonly Mock<IPeerPublicSigningKeyStore> _keyStoreMock;
    private readonly ILogger<DispatchTextMessageHandler> _logger;
    private readonly DispatchTextMessageHandler _sut;
    private readonly PeerId _selfId = new(Guid.NewGuid());
    private readonly PeerId _recipientId = new(Guid.NewGuid());
    private readonly Guid _messageId = Guid.NewGuid();
    private readonly string _content = "Test message";
    private readonly DateTime _sentUtc = DateTime.UtcNow;

    public DispatchTextMessageHandlerTests()
    {
        _mediatorMock = new Mock<IMediator>(MockBehavior.Strict);
        _senderMock = new Mock<IRemoteEnvelopeSender>(MockBehavior.Strict);
        _keyStoreMock = new Mock<IPeerPublicSigningKeyStore>(MockBehavior.Strict);
        _logger = NullLogger<DispatchTextMessageHandler>.Instance;
        _sut = new DispatchTextMessageHandler(
            _mediatorMock.Object,
            _senderMock.Object,
            _keyStoreMock.Object,
            _logger);

        // app-level primitives prepared in fields
    }

    [Test]
    public async Task Handle_SendsChatEnvelopeToRecipient_ViaSender()
    {
        // Arrange
        var recipients = new[] { _recipientId };
        var command = new DispatchTextMessageCommand(_messageId, _content, _sentUtc, recipients);

        _keyStoreMock
            .Setup(k => k.GetPublicKeyHashByPeerIdAsync(_recipientId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((byte[]?)null);

        _senderMock
            .Setup(s => s.SendChatEnvelopeToPeerAsync(
                It.Is<ChatEnvelope>(e => e.TextMessage != null &&
                    e.TextMessage.MessageId.ToByteArray().SequenceEqual(_messageId.ToByteArray()) &&
                    e.TextMessage.Content == _content),
                It.Is<RecipientRoute>(r => r.PeerId.Equals(_recipientId)),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask)
            .Verifiable();

        // Act
        await _sut.Handle(command, CancellationToken.None);

        // Assert
        _senderMock.Verify(s => s.SendChatEnvelopeToPeerAsync(
            It.IsAny<ChatEnvelope>(),
            It.Is<RecipientRoute>(r => r.PeerId.Equals(_recipientId)),
            It.IsAny<CancellationToken>()), Times.Once);
        _senderMock.Verify(s => s.SendChatEnvelopeToPeerAsync(
            It.IsAny<ChatEnvelope>(),
            It.Is<RecipientRoute>(r => r.PeerId.Equals(_selfId)),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Test]
    public async Task Handle_SendsToAllRecipients_IncludingSelf_WhenPresent()
    {
        // Arrange
        var recipients = new[] { _recipientId, _selfId };
        var command = new DispatchTextMessageCommand(_messageId, _content, _sentUtc, recipients);

        _keyStoreMock
            .Setup(k => k.GetPublicKeyHashByPeerIdAsync(_recipientId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((byte[]?)null);
        _keyStoreMock
            .Setup(k => k.GetPublicKeyHashByPeerIdAsync(_selfId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((byte[]?)null);

        _senderMock
            .Setup(s => s.SendChatEnvelopeToPeerAsync(
                It.IsAny<ChatEnvelope>(),
                It.Is<RecipientRoute>(r => r.PeerId.Equals(_recipientId)),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask)
            .Verifiable();
        _senderMock
            .Setup(s => s.SendChatEnvelopeToPeerAsync(
                It.IsAny<ChatEnvelope>(),
                It.Is<RecipientRoute>(r => r.PeerId.Equals(_selfId)),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask)
            .Verifiable();

        // Act
        await _sut.Handle(command, CancellationToken.None);

        // Assert
        _senderMock.Verify();
    }
}

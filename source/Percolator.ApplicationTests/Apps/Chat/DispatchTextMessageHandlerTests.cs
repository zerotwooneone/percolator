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
using Percolator.MessageQueue.Commands;
using Percolator.MessageQueue.Results;
using Percolator.Network;

// Alias to disambiguate between the two PeerId types
using PeerId = Percolator.Identity.PeerId;

namespace Percolator.ApplicationTests.Apps.Chat;

public class DispatchTextMessageHandlerTests
{
    private readonly Mock<IMediator> _mediatorMock;
    private readonly Mock<IMessageTransportService> _transportMock;
    private readonly Mock<IDirectSessionRepository> _sessionRepoMock;
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
        _transportMock = new Mock<IMessageTransportService>(MockBehavior.Strict);
        _sessionRepoMock = new Mock<IDirectSessionRepository>(MockBehavior.Strict);
        _logger = NullLogger<DispatchTextMessageHandler>.Instance;
        _sut = new DispatchTextMessageHandler(
            _mediatorMock.Object, 
            _transportMock.Object, 
            _sessionRepoMock.Object, 
            _logger);

        // app-level primitives prepared in fields
    }

    [Test]
    public async Task Handle_EnqueuesMessageForRecipient()
    {
        // Arrange
        var recipients = new[] { _recipientId };
        var command = new DispatchTextMessageCommand(_messageId, _content, _sentUtc, recipients, _selfId);
        
        // Mock the mediator to handle the EnqueueOpaqueMessageCommand
        _mediatorMock
            .Setup(m => m.Send(
                It.Is<EnqueueOpaqueMessageCommand>(c => 
                    c.RecipientPublicKeyHash.SequenceEqual(_recipientId.Value.ToByteArray()) &&
                    c.MessageBlob != null),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new EnqueueOpaqueMessageResult(true, null));

        // Act
        await _sut.Handle(command, CancellationToken.None);

        // Assert
        _mediatorMock.VerifyAll();
        _transportMock.Verify(t => t.SendMessageAsync(
            It.IsAny<PeerId>(),
            It.IsAny<DirectSessionId>(),
            It.IsAny<SessionRatchetMessage>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Test]
    public async Task Handle_WhenEnqueueRejected_DoesNotAttemptTransport()
    {
        // Arrange
        var recipients = new[] { _recipientId };
        var command = new DispatchTextMessageCommand(_messageId, _content, _sentUtc, recipients, _selfId);

        _mediatorMock
            .Setup(m => m.Send(
                It.Is<EnqueueOpaqueMessageCommand>(c =>
                    c.RecipientPublicKeyHash.SequenceEqual(_recipientId.Value.ToByteArray()) &&
                    c.MessageBlob != null),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new EnqueueOpaqueMessageResult(false, "rejected"));

        // Act
        await _sut.Handle(command, CancellationToken.None);

        // Assert
        _mediatorMock.VerifyAll();
        _transportMock.Verify(t => t.SendMessageAsync(
            It.IsAny<PeerId>(),
            It.IsAny<DirectSessionId>(),
            It.IsAny<SessionRatchetMessage>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }
}

using MediatR;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Percolator.Application.Apps.Chat;
using Percolator.Application.Apps.Chat.Commands;
using Percolator.Application.Apps.Chat.Handlers;
using Percolator.Identity;
using Percolator.Application.Network;

namespace Percolator.ApplicationTests.Apps.Chat;

public class DispatchDeliveredReceiptHandlerTests
{
    private readonly Mock<IMediator> _mediatorMock;
    private readonly Mock<IRemoteEnvelopeSender> _senderMock;
    private readonly Mock<IPeerPublicSigningKeyStore> _keyStoreMock;
    private readonly ILogger<DispatchDeliveredReceiptHandler> _logger;
    private readonly DispatchDeliveredReceiptHandler _sut;
    private readonly PeerId _selfId = new(Guid.NewGuid());
    private readonly PeerId _recipientId = new(Guid.NewGuid());
    private readonly Guid _messageId = Guid.NewGuid();
    private readonly DateTime _sentUtc = DateTime.UtcNow;

    public DispatchDeliveredReceiptHandlerTests()
    {
        _mediatorMock = new Mock<IMediator>(MockBehavior.Strict);
        _senderMock = new Mock<IRemoteEnvelopeSender>(MockBehavior.Strict);
        _keyStoreMock = new Mock<IPeerPublicSigningKeyStore>(MockBehavior.Strict);
        _logger = NullLogger<DispatchDeliveredReceiptHandler>.Instance;
        _sut = new DispatchDeliveredReceiptHandler(_mediatorMock.Object, _senderMock.Object, _keyStoreMock.Object, _logger);
    }

    [Test]
    public async Task Handle_SendsViaSender_PerRecipient()
    {
        // Arrange
        var recipients = new[] { _recipientId };
        var command = new DispatchDeliveredReceiptCommand(_messageId, _sentUtc, recipients, _selfId);

        _senderMock
            .Setup(s => s.SendChatEnvelopeToPeerAsync(
                It.IsAny<Percolator.Contracts.ChatEnvelope>(),
                It.Is<PeerId>(p => p.Equals(_recipientId)),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask)
            .Verifiable();

        // Act
        await _sut.Handle(command, CancellationToken.None);

        // Assert
        _senderMock.Verify();
    }
}

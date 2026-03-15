using MediatR;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Percolator.Application.Apps.Chat;
using Percolator.Identity;
using Percolator.Application.Network;

namespace Percolator.ApplicationTests.Apps.Chat;

public class DispatchEmojiAnnotationHandlerTests
{
    private readonly Mock<IMediator> _mediatorMock;
    private readonly Mock<IRemoteEnvelopeSender> _senderMock;
    private readonly Mock<IPeerPublicSigningKeyStore> _keyStoreMock;
    private readonly ILogger<DispatchEmojiAnnotationHandler> _logger;
    private readonly DispatchEmojiAnnotationHandler _sut;
    private readonly PeerId _selfId = new(Guid.NewGuid());
    private readonly PeerId _recipientId = new(Guid.NewGuid());
    private readonly Guid _messageId = Guid.NewGuid();
    private readonly string _emoji = "👍";
    private readonly DateTime _sentUtc = DateTime.UtcNow;

    public DispatchEmojiAnnotationHandlerTests()
    {
        _mediatorMock = new Mock<IMediator>(MockBehavior.Strict);
        _senderMock = new Mock<IRemoteEnvelopeSender>(MockBehavior.Strict);
        _keyStoreMock = new Mock<IPeerPublicSigningKeyStore>(MockBehavior.Strict);
        _logger = NullLogger<DispatchEmojiAnnotationHandler>.Instance;
        _sut = new DispatchEmojiAnnotationHandler(_mediatorMock.Object, _senderMock.Object, _keyStoreMock.Object, _logger);
    }

    [Test]
    public async Task Handle_SendsViaSender_PerRecipient()
    {
        // Arrange
        var recipients = new[] { _recipientId };
        var command = new DispatchEmojiAnnotationCommand(_messageId, _emoji, _sentUtc, recipients, _selfId);

        _keyStoreMock
            .Setup(k => k.GetPublicKeyHashByPeerIdAsync(_recipientId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((byte[]?)null);
        _senderMock
            .Setup(s => s.SendChatEnvelopeToPeerAsync(
                It.IsAny<Percolator.Contracts.ChatEnvelope>(),
                It.Is<RecipientRoute>(r => r.PeerId.Equals(_recipientId)),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask)
            .Verifiable();

        // Act
        await _sut.Handle(command, CancellationToken.None);

        // Assert
        _senderMock.Verify();
    }
}

using MediatR;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Percolator.Application.Apps.Chat;
using Percolator.Identity;
using Percolator.Application.Network;

namespace Percolator.ApplicationTests.Apps.Chat;

[TestFixture]
public class DispatchSignedAdminCommitOperationHandlerTests
{
    private Mock<IMediator> _mediatorMock = null!;
    private Mock<IRemoteEnvelopeSender> _senderMock = null!;
    private Mock<IPeerPublicSigningKeyStore> _keyStoreMock = null!;
    private ILogger<DispatchSignedAdminCommitOperationHandler> _logger = null!;
    private DispatchSignedAdminCommitOperationHandler _sut = null!;

    private PeerId _senderId = new(Guid.NewGuid());
    private PeerId _recipientId = new PeerId(Guid.NewGuid());
    private Guid _groupId = Guid.NewGuid();
    private Guid _opId = Guid.NewGuid();
    private DateTime _sentUtc = DateTime.UtcNow;

    [SetUp]
    public void SetUp()
    {
        _mediatorMock = new Mock<IMediator>(MockBehavior.Strict);
        _senderMock = new Mock<IRemoteEnvelopeSender>(MockBehavior.Strict);
        _keyStoreMock = new Mock<IPeerPublicSigningKeyStore>(MockBehavior.Strict);
        _logger = NullLogger<DispatchSignedAdminCommitOperationHandler>.Instance;
        _sut = new DispatchSignedAdminCommitOperationHandler(_mediatorMock.Object, _senderMock.Object, _keyStoreMock.Object, _logger);
    }

    [Test]
    public async Task Handle_SendsViaSender_PerRecipient()
    {
        // Arrange
        var recipients = new[] { _recipientId };
        var cmd = new DispatchSignedAdminCommitOperationCommand(
            _groupId,
            _opId,
            7,
            _sentUtc,
            recipients,
            _senderId,
            new byte[] { 9, 9, 9 },
            null);

        _keyStoreMock
            .Setup(k => k.GetPublicKeyHashByPeerIdAsync(_recipientId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((IdentityPublicKeyHash?)null);
        _senderMock
            .Setup(s => s.SendChatEnvelopeToPeerAsync(
                It.IsAny<Percolator.Contracts.ChatEnvelope>(),
                It.Is<RecipientRoute>(r => r.PeerId.Equals(_recipientId)),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask)
            .Verifiable();

        // Act
        await _sut.Handle(cmd, CancellationToken.None);

        // Assert
        _senderMock.Verify();
    }
}

using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using Percolator.Identity;
using Percolator.MessageQueue.Abstractions;
using Percolator.MessageQueue.Commands;
using Percolator.MessageQueue.Handlers;

namespace Percolator.MessageQueueTests;

[TestFixture]
public class FetchQueuedMessagesHandlerTests
{
    [Test]
    public async Task Defaults_to_100_when_non_positive_max()
    {
        var repo = new Mock<IMessageQueueRepository>(MockBehavior.Strict);
        var logger = new Mock<ILogger<FetchQueuedMessagesHandler>>();
        var sut = new FetchQueuedMessagesHandler(logger.Object, repo.Object);
        var peerId = PeerId.NewId();

        repo.Setup(r => r.FetchAsync(peerId, 100, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<(Guid, byte[])>());

        var result = await sut.Handle(new FetchQueuedMessagesQuery(peerId, 0), CancellationToken.None);

        result.Messages.Should().BeEmpty();
        repo.VerifyAll();
    }

    [Test]
    public async Task Caps_at_500_when_requested_exceeds_limit()
    {
        var repo = new Mock<IMessageQueueRepository>(MockBehavior.Strict);
        var logger = new Mock<ILogger<FetchQueuedMessagesHandler>>();
        var sut = new FetchQueuedMessagesHandler(logger.Object, repo.Object);
        var peerId = PeerId.NewId();

        repo.Setup(r => r.FetchAsync(peerId, 500, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<(Guid, byte[])>());

        var result = await sut.Handle(new FetchQueuedMessagesQuery(peerId, 10_000), CancellationToken.None);

        result.Messages.Should().BeEmpty();
        repo.VerifyAll();
    }

    [Test]
    public async Task Passes_through_order_from_repository_oldest_first()
    {
        var repo = new Mock<IMessageQueueRepository>(MockBehavior.Strict);
        var logger = new Mock<ILogger<FetchQueuedMessagesHandler>>();
        var sut = new FetchQueuedMessagesHandler(logger.Object, repo.Object);
        var peerId = PeerId.NewId();
        var msg1 = new byte[] { 0x01, 0x02 };
        var msg2 = new byte[] { 0x03 };

        repo.Setup(r => r.FetchAsync(peerId, 2, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { (Guid.NewGuid(), msg1), (Guid.NewGuid(), msg2) });

        var result = await sut.Handle(new FetchQueuedMessagesQuery(peerId, 2), CancellationToken.None);

        result.Messages.Should().HaveCount(2);
        result.Messages[0].Should().BeSameAs(msg1);
        result.Messages[1].Should().BeSameAs(msg2);
        repo.VerifyAll();
    }

    
}

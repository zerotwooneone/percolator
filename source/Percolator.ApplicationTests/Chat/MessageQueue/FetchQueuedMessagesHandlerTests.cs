using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using Percolator.Application.Chat.MessageQueue;
using Percolator.Application.Chat.MessageQueue.Commands;
using Percolator.Application.Chat.MessageQueue.Handlers;
using Percolator.Chat.Messaging.ValueObjects;
using Percolator.Identity;

namespace Percolator.ApplicationTests.Chat.MessageQueue;

[TestFixture]
public class FetchQueuedMessagesHandlerTests
{
    [Test]
    public async Task Defaults_to_100_when_non_positive_max()
    {
        var repo = new Mock<IMessageQueueRepository>(MockBehavior.Loose);
        var logger = new Mock<ILogger<FetchQueuedMessagesHandler>>();
        var sut = new FetchQueuedMessagesHandler(logger.Object, repo.Object);
        var peerId = new PeerId(1);

        repo.Setup(r => r.FetchAsync(peerId, 100, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<(Guid, QueuedPayloadBytes)>());

        var result = await sut.Handle(new FetchQueuedMessagesQuery(peerId, 0), CancellationToken.None);

        result.Messages.Should().BeEmpty();
    }

    [Test]
    public async Task Caps_at_500_when_requested_exceeds_limit()
    {
        var repo = new Mock<IMessageQueueRepository>(MockBehavior.Loose);
        var logger = new Mock<ILogger<FetchQueuedMessagesHandler>>();
        var sut = new FetchQueuedMessagesHandler(logger.Object, repo.Object);
        var peerId = new PeerId(1);

        repo.Setup(r => r.FetchAsync(peerId, 500, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<(Guid, QueuedPayloadBytes)>());

        var result = await sut.Handle(new FetchQueuedMessagesQuery(peerId, 10_000), CancellationToken.None);

        result.Messages.Should().BeEmpty();
    }

    [Test]
    public async Task Passes_through_order_from_repository_oldest_first()
    {
        var repo = new Mock<IMessageQueueRepository>(MockBehavior.Loose);
        var logger = new Mock<ILogger<FetchQueuedMessagesHandler>>();
        var sut = new FetchQueuedMessagesHandler(logger.Object, repo.Object);
        var peerId = new PeerId(1);
        var msg1 = new byte[] { 0x01, 0x02 };
        var msg2 = new byte[] { 0x03 };

        repo.Setup(r => r.FetchAsync(peerId, 2, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { (Guid.NewGuid(), QueuedPayloadBytes.FromBytesOwned(msg1)), (Guid.NewGuid(), QueuedPayloadBytes.FromBytesOwned(msg2)) });

        var result = await sut.Handle(new FetchQueuedMessagesQuery(peerId, 2), CancellationToken.None);

        result.Messages.Should().HaveCount(2);
        result.Messages[0].Should().BeEquivalentTo(msg1);
        result.Messages[1].Should().BeEquivalentTo(msg2);
    }

    
}

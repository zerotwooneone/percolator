using MediatR;
using Percolator.Chat.Events;

namespace Percolator.Chat.App.Commands;

public sealed class PostReadReceiptHandler : IRequestHandler<PostReadReceiptCommand>
{
    private readonly IConversationResolver _resolver;
    private readonly IChatMessageWriter _writer;
    private readonly IPublisher _publisher;

    public PostReadReceiptHandler(IConversationResolver resolver, IChatMessageWriter writer, IPublisher publisher)
    {
        _resolver = resolver;
        _writer = writer;
        _publisher = publisher;
    }

    public async Task Handle(PostReadReceiptCommand request, CancellationToken cancellationToken)
    {
        request.LookupKey.EnsureExactlyOne();
        var resolution = await _resolver.ResolveAsync(request.LookupKey, cancellationToken);
        await _writer.AddReadReceiptAsync(
            resolution.Conversation.Id,
            resolution.SelfIdentityId,
            request.MessageId,
            request.SentTimestampUtc,
            cancellationToken);

        // Publish event to application layer for dispatch over MQ
        await _publisher.Publish(new ReadReceiptPostedEvent(
            resolution.Conversation.Id.Value,
            request.MessageId.Value,
            resolution.SelfIdentityId,
            resolution.Conversation.Participants
                .Select(p => p.Value.ToByteArray()
                    .Take(8)
                    .Select((b, i) => (long)b << (i * 8))
                    .Aggregate((x, y) => x | y))
                .ToArray(),
            request.SentTimestampUtc.UtcDateTime),
            cancellationToken);
    }
}

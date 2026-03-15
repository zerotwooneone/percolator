using MediatR;
using Percolator.Chat.Events;

namespace Percolator.Chat.App.Commands;

public sealed class PostDeliveredReceiptHandler : IRequestHandler<PostDeliveredReceiptCommand>
{
    private readonly IConversationResolver _resolver;
    private readonly IChatMessageWriter _writer;
    private readonly IPublisher _publisher;

    public PostDeliveredReceiptHandler(IConversationResolver resolver, IChatMessageWriter writer, IPublisher publisher)
    {
        _resolver = resolver;
        _writer = writer;
        _publisher = publisher;
    }

    public async Task Handle(PostDeliveredReceiptCommand request, CancellationToken cancellationToken)
    {
        request.LookupKey.EnsureExactlyOne();
        var resolution = await _resolver.ResolveAsync(request.LookupKey, cancellationToken);
        await _writer.AddDeliveredReceiptAsync(
            resolution.Conversation.Id,
            resolution.SelfIdentityId,
            request.MessageId,
            request.DeliveredAtUtc,
            cancellationToken);

        await _publisher.Publish(new DeliveredReceiptPostedEvent(
            resolution.Conversation.Id.Value,
            request.MessageId.Value,
            resolution.SelfIdentityId,
            resolution.Conversation.Participants
                .Select(p => p.Value.ToByteArray()
                    .Take(8)
                    .Select((b, i) => (long)b << (i * 8))
                    .Aggregate((x, y) => x | y))
                .ToArray(),
            request.DeliveredAtUtc.UtcDateTime),
            cancellationToken);
    }
}

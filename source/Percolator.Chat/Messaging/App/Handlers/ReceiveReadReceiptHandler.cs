using MediatR;
using Percolator.Chat.Messaging.App.Commands;
using Percolator.Chat.Messaging.Events;

namespace Percolator.Chat.Messaging.App.Handlers;

public sealed class ReceiveReadReceiptHandler : IRequestHandler<ReceiveReadReceiptCommand>
{
    private readonly IDirectConversationResolver _resolver;
    private readonly IChatMessageWriter _writer;
    private readonly IPublisher _publisher;

    public ReceiveReadReceiptHandler(
        IDirectConversationResolver resolver,
        IChatMessageWriter writer,
        IPublisher publisher)
    {
        _resolver = resolver;
        _writer = writer;
        _publisher = publisher;
    }

    public async Task Handle(ReceiveReadReceiptCommand request, CancellationToken cancellationToken)
    {
        request.LookupKey.EnsureExactlyOne();
        var resolution = await _resolver.ResolveAsync(request.LookupKey, cancellationToken).ConfigureAwait(false);

        await _writer.AddReadReceiptAsync(
            resolution.Conversation.Id,
            resolution.SelfIdentityId,
            request.ReaderId,
            request.MessageId,
            request.SentTimestampUtc,
            cancellationToken).ConfigureAwait(false);

        await _publisher.Publish(new ReadReceiptReceivedEvent(
            resolution.Conversation.Id.Value,
            request.MessageId.Value,
            resolution.SelfIdentityId,
            request.ReaderId.Value,
            request.SentTimestampUtc), cancellationToken).ConfigureAwait(false);
    }
}

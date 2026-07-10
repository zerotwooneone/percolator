using MediatR;
using Percolator.Chat.Messaging.App.Commands;
using Percolator.Chat.Messaging.Events;
using Percolator.Chat.Messaging.ValueObjects;

namespace Percolator.Chat.Messaging.App.Handlers;

public sealed class ReceiveTextMessageHandler : IRequestHandler<ReceiveTextMessageCommand>
{
    private readonly IDirectConversationResolver _resolver;
    private readonly IChatMessageWriter _writer;
    private readonly IPublisher _publisher;

    public ReceiveTextMessageHandler(
        IDirectConversationResolver resolver,
        IChatMessageWriter writer,
        IPublisher publisher)
    {
        _resolver = resolver;
        _writer = writer;
        _publisher = publisher;
    }

    public async Task Handle(ReceiveTextMessageCommand request, CancellationToken cancellationToken)
    {
        request.LookupKey.EnsureExactlyOne();
        var resolution = await _resolver.ResolveAsync(request.LookupKey, cancellationToken).ConfigureAwait(false);

        await _writer.AddTextMessageAsync(
            resolution.Conversation.Id,
            request.SenderId,
            request.Content,
            request.PublicMessageId,
            request.SentTimestampUtc,
            cancellationToken).ConfigureAwait(false);

        await _publisher.Publish(new TextMessageReceivedEvent(
            resolution.Conversation.Id,
            request.PublicMessageId,
            resolution.SelfIdentityId,
            request.SenderId,
            request.Content,
            request.SentTimestampUtc,
            DirectSessionIdValueObject.FromGuid(request.LookupKey.DirectSessionId)), cancellationToken).ConfigureAwait(false);
    }
}

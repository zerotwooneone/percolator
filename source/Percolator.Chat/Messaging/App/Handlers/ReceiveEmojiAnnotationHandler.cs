using MediatR;
using Percolator.Chat.GroupMembership;
using Percolator.Chat.Messaging.App.Commands;
using Percolator.Chat.Messaging.Events;

namespace Percolator.Chat.Messaging.App.Handlers;

public sealed class ReceiveEmojiAnnotationHandler : IRequestHandler<ReceiveEmojiAnnotationCommand>
{
    private readonly IDirectConversationResolver _resolver;
    private readonly IChatMessageWriter _writer;
    private readonly IPublisher _publisher;

    public ReceiveEmojiAnnotationHandler(
        IDirectConversationResolver resolver,
        IChatMessageWriter writer,
        IPublisher publisher)
    {
        _resolver = resolver;
        _writer = writer;
        _publisher = publisher;
    }

    public async Task Handle(ReceiveEmojiAnnotationCommand request, CancellationToken cancellationToken)
    {
        request.LookupKey.EnsureExactlyOne();
        var resolution = await _resolver.ResolveAsync(request.LookupKey, cancellationToken).ConfigureAwait(false);

        await _writer.AddEmojiAnnotationAsync(
            resolution.Conversation.Id,
            resolution.SelfIdentityId,
            request.ReactorId,
            request.MessageId,
            request.Emoji,
            request.SentTimestampUtc,
            cancellationToken).ConfigureAwait(false);

        await _publisher.Publish(new EmojiAnnotationReceivedEvent(
            resolution.Conversation.Id.Value,
            request.MessageId.Value,
            resolution.SelfIdentityId,
            new ChatPeerId(request.ReactorId.Value),
            request.Emoji,
            request.SentTimestampUtc), cancellationToken).ConfigureAwait(false);
    }
}

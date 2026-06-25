using MediatR;
using Percolator.Application.Identity;
using Percolator.Chat;
using Percolator.Chat.GroupMembership;
using Percolator.Chat.Messaging.App;
using Percolator.Chat.Messaging.App.Commands;
using Percolator.Chat.Messaging.Events;
using Percolator.Chat.Messaging.ValueObjects;

namespace Percolator.Application.Apps.Chat.Handlers;

public sealed class PostTextMessageHandler : IRequestHandler<PostTextMessageCommand>
{
    private readonly IDirectConversationResolver _resolver;
    private readonly IChatMessageWriter _writer;
    private readonly IPublisher _publisher;
    private readonly ActiveIdentityContext _active;

    public PostTextMessageHandler(
        IDirectConversationResolver resolver,
        IChatMessageWriter writer,
        IPublisher publisher,
        ActiveIdentityContext active)
    {
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
        _writer = writer ?? throw new ArgumentNullException(nameof(writer));
        _publisher = publisher ?? throw new ArgumentNullException(nameof(publisher));
        _active = active ?? throw new ArgumentNullException(nameof(active));
    }

    public async Task Handle(PostTextMessageCommand request, CancellationToken cancellationToken)
    {
        request.LookupKey.EnsureExactlyOne();

        var resolution = await _resolver.ResolveAsync(request.LookupKey, cancellationToken).ConfigureAwait(false);
        var selfParticipantId = ((ISelfParticipantIdProvider) _active).Get();

        await _writer.AddTextMessageAsync(
            resolution.Conversation.Id,
            resolution.SelfIdentityId,
            selfParticipantId,
            request.Content,
            request.MessageId,
            request.SentTimestampUtc,
            cancellationToken).ConfigureAwait(false);

        await _publisher.Publish(new TextMessagePostedEvent(
                resolution.Conversation.Id.Value,
                request.MessageId.Value,
                resolution.SelfIdentityId,
                new[] { new ChatPeerId(resolution.Conversation.Peer1.Value), new ChatPeerId(resolution.Conversation.Peer2.Value) },
                request.Content,
                request.SentTimestampUtc,
                DirectSessionIdValueObject.FromGuid(request.LookupKey.DirectSessionId)),
            cancellationToken).ConfigureAwait(false);
    }
}

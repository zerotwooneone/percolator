using Google.Protobuf;
using MediatR;
using Percolator.Application.Identity;
using Percolator.Chat;
using Percolator.Chat.App;
using Percolator.Chat.App.Commands;
using Percolator.Chat.Events;
using Percolator.Chat.ValueObjects;

namespace Percolator.Application.Apps.Chat.Handlers;

public sealed class PostTextMessageHandler : IRequestHandler<PostTextMessageCommand>
{
    private readonly IConversationResolver _resolver;
    private readonly IChatMessageWriter _writer;
    private readonly IPublisher _publisher;
    private readonly ActiveIdentityContext _active;

    public PostTextMessageHandler(
        IConversationResolver resolver,
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
                resolution.Conversation.Participants
                    .Select(p => p.Value)
                    .ToList(),
                request.Content,
                request.SentTimestampUtc,
                Percolator.Chat.ValueObjects.DirectSessionIdValueObject.FromGuid(request.LookupKey.DirectSessionId)),
            cancellationToken).ConfigureAwait(false);
    }
}

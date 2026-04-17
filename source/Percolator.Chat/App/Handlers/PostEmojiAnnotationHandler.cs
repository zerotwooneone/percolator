using MediatR;
using Percolator.Chat.Events;

namespace Percolator.Chat.App.Commands;

public sealed class PostEmojiAnnotationHandler : IRequestHandler<PostEmojiAnnotationCommand>
{
    private readonly IConversationResolver _resolver;
    private readonly IChatMessageWriter _writer;
    private readonly IPublisher _publisher;
    private readonly ISelfParticipantIdProvider _selfParticipantIdProvider;

    public PostEmojiAnnotationHandler(IConversationResolver resolver, IChatMessageWriter writer, IPublisher publisher, ISelfParticipantIdProvider selfParticipantIdProvider)
    {
        _resolver = resolver;
        _writer = writer;
        _publisher = publisher;
        _selfParticipantIdProvider = selfParticipantIdProvider;
    }

    public async Task Handle(PostEmojiAnnotationCommand request, CancellationToken cancellationToken)
    {
        request.LookupKey.EnsureExactlyOne();
        var resolution = await _resolver.ResolveAsync(request.LookupKey, cancellationToken);
        var selfParticipantId = _selfParticipantIdProvider.Get();
        
        await _writer.AddEmojiAnnotationAsync(
            resolution.Conversation.Id,
            resolution.SelfIdentityId,
            selfParticipantId,
            request.MessageId,
            request.Emoji,
            request.SentTimestampUtc,
            cancellationToken);

        await _publisher.Publish(new EmojiAnnotationPostedEvent(
            resolution.Conversation.Id.Value,
            request.MessageId.Value,
            request.Emoji,
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

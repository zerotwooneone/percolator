using MediatR;
using Percolator.Chat.Events;

namespace Percolator.Chat.App.Commands;

public sealed class PostReadReceiptHandler : IRequestHandler<PostReadReceiptCommand>
{
    private readonly IDirectConversationResolver _resolver;
    private readonly IChatMessageWriter _writer;
    private readonly IPublisher _publisher;
    private readonly ISelfParticipantIdProvider _selfParticipantIdProvider;

    public PostReadReceiptHandler(IDirectConversationResolver resolver, IChatMessageWriter writer, IPublisher publisher, ISelfParticipantIdProvider selfParticipantIdProvider)
    {
        _resolver = resolver;
        _writer = writer;
        _publisher = publisher;
        _selfParticipantIdProvider = selfParticipantIdProvider;
    }

    public async Task Handle(PostReadReceiptCommand request, CancellationToken cancellationToken)
    {
        request.LookupKey.EnsureExactlyOne();
        var resolution = await _resolver.ResolveAsync(request.LookupKey, cancellationToken);
        var selfParticipantId = _selfParticipantIdProvider.Get();

        await _writer.AddReadReceiptAsync(
            resolution.Conversation.Id,
            resolution.SelfIdentityId,
            selfParticipantId,
            request.MessageId,
            request.SentTimestampUtc,
            cancellationToken);

        // Publish event to application layer for dispatch over MQ
        await _publisher.Publish(new ReadReceiptPostedEvent(
            resolution.Conversation.Id.Value,
            request.MessageId.Value,
            resolution.SelfIdentityId,
            new[] { BitConverter.ToInt64(resolution.Conversation.Peer1.Value.ToByteArray()), BitConverter.ToInt64(resolution.Conversation.Peer2.Value.ToByteArray()) },
            request.SentTimestampUtc.UtcDateTime),
            cancellationToken);
    }
}

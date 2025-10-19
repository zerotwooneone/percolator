using MediatR;
using Percolator.Chat.App;
using Percolator.Chat.Events;
using Percolator.Chat.ValueObjects;

namespace Percolator.Chat.App.Commands;

public sealed class PostReadReceiptHandler : IRequestHandler<PostReadReceiptCommand>
{
    private readonly IConversationResolver _resolver;
    private readonly IChatMessageWriter _writer;
    private readonly IPublisher _publisher;
    private readonly ISelfIdentityProvider _selfIdentityProvider;

    public PostReadReceiptHandler(IConversationResolver resolver, IChatMessageWriter writer, IPublisher publisher, ISelfIdentityProvider selfIdentityProvider)
    {
        _resolver = resolver;
        _writer = writer;
        _publisher = publisher;
        _selfIdentityProvider = selfIdentityProvider;
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

        // Get the peer ID for the self identity
        var peerId = await _selfIdentityProvider.GetPeerIdAsync(resolution.SelfIdentityId, cancellationToken);
        var selfParticipantId = new ParticipantId(peerId);

        // Publish event to application layer for dispatch over MQ
        await _publisher.Publish(new ReadReceiptPostedEvent(
            resolution.Conversation.Id.Value,
            request.MessageId.Value,
            resolution.SelfIdentityId,
            resolution.Conversation.Participants
                .Where(p => p != selfParticipantId)
                .Select(p => p.Value.ToByteArray()
                    .Take(8)
                    .Select((b, i) => (long)b << (i * 8))
                    .Aggregate((x, y) => x | y))
                .ToArray(),
            request.SentTimestampUtc.UtcDateTime),
            cancellationToken);
    }
}

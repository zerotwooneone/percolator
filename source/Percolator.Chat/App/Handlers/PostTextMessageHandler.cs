using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediatR;
using Percolator.Chat.App;
using Percolator.Chat.Events;
using Percolator.Chat.ValueObjects;

namespace Percolator.Chat.App.Commands;

public sealed class PostTextMessageHandler : IRequestHandler<PostTextMessageCommand>
{
    private readonly IConversationResolver _resolver;
    private readonly IChatMessageWriter _writer;
    private readonly IPublisher _publisher;
    private readonly ISelfIdentityProvider _selfIdentityProvider;

    public PostTextMessageHandler(
        IConversationResolver resolver, 
        IChatMessageWriter writer,
        IPublisher publisher,
        ISelfIdentityProvider selfIdentityProvider)
    {
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
        _writer = writer ?? throw new ArgumentNullException(nameof(writer));
        _publisher = publisher ?? throw new ArgumentNullException(nameof(publisher));
        _selfIdentityProvider = selfIdentityProvider ?? throw new ArgumentNullException(nameof(selfIdentityProvider));
    }

    public async Task Handle(PostTextMessageCommand request, CancellationToken cancellationToken)
    {
        // Enforce exactly-one-key rule at the application boundary of Chat domain
        request.LookupKey.EnsureExactlyOne();

        // Resolve conversation context (implementation will be provided in Infrastructure later)
        var resolution = await _resolver.ResolveAsync(request.LookupKey, cancellationToken);

        // Persist text message with DB-enforced idempotency (UNIQUE ConversationId+MessageGuid)
        await _writer.AddTextMessageAsync(
            resolution.Conversation.Id,
            resolution.SelfIdentityId,
            request.Content,
            request.MessageId,
            request.SentTimestampUtc,
            cancellationToken);

        // Get the peer ID for the self identity
        var peerId = await _selfIdentityProvider.GetPeerIdAsync(resolution.SelfIdentityId, cancellationToken);
        var selfParticipantId = new ParticipantId(peerId);
        
        // Publish an event that the message was posted
        await _publisher.Publish(new TextMessagePostedEvent(
            resolution.Conversation.Id.Value, // Convert ConversationId to Guid
            request.MessageId.Value,          // Convert MessageId to Guid
            resolution.SelfIdentityId,        // Keep as int for the event
            resolution.Conversation.Participants
                .Where(p => p != selfParticipantId)  // Exclude self from recipients
                .Select(p => p.Value.ToByteArray()   // Convert Guid to byte[]
                    .Take(8)                        // Take first 8 bytes
                    .Select((b, i) => (long)b << (i * 8))  // Convert each byte to long with proper bit shifting
                    .Aggregate((x, y) => x | y))    // Combine bytes into a single long
                .ToArray(),
            request.Content,
            request.SentTimestampUtc.UtcDateTime),   // Convert DateTimeOffset to DateTime
            cancellationToken);
    }
}

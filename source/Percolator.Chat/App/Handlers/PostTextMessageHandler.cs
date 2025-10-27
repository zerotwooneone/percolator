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

    public PostTextMessageHandler(
        IConversationResolver resolver,
        IChatMessageWriter writer,
        IPublisher publisher)
    {
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
        _writer = writer ?? throw new ArgumentNullException(nameof(writer));
        _publisher = publisher ?? throw new ArgumentNullException(nameof(publisher));
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
        
        // Publish an event that the message was posted
        await _publisher.Publish(new TextMessagePostedEvent(
            resolution.Conversation.Id.Value,
            request.MessageId.Value,
            resolution.SelfIdentityId,
            resolution.Conversation.Participants
                .Select(p => p.Value)
                .ToList(),
            request.Content,
            request.SentTimestampUtc.UtcDateTime),
            cancellationToken);
    }
}

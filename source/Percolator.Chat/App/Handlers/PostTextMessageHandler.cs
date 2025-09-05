using MediatR;
using Percolator.Chat.App;

namespace Percolator.Chat.App.Commands;

public sealed class PostTextMessageHandler : IRequestHandler<PostTextMessageCommand>
{
    private readonly IConversationResolver _resolver;
    private readonly IChatMessageWriter _writer;

    public PostTextMessageHandler(IConversationResolver resolver, IChatMessageWriter writer)
    {
        _resolver = resolver;
        _writer = writer;
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
    }
}

using MediatR;

namespace Percolator.Chat.App.Commands;

public sealed class PostTextMessageHandler : IRequestHandler<PostTextMessageCommand>
{
    private readonly IConversationResolver _resolver;

    public PostTextMessageHandler(IConversationResolver resolver)
    {
        _resolver = resolver;
    }

    public async Task Handle(PostTextMessageCommand request, CancellationToken cancellationToken)
    {
        // Enforce exactly-one-key rule at the application boundary of Chat domain
        request.LookupKey.EnsureExactlyOne();

        // Resolve conversation context (implementation will be provided in Infrastructure later)
        var resolution = await _resolver.ResolveAsync(request.LookupKey, cancellationToken);

        // TODO: Persist message with idempotency at Infrastructure level.
        // For now, no-op domain mutation to keep build green until repository and schema are ready.
        _ = resolution;
    }
}

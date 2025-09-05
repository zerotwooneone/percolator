using MediatR;
using Percolator.Chat.App;

namespace Percolator.Chat.App.Commands;

public sealed class PostReadReceiptHandler : IRequestHandler<PostReadReceiptCommand>
{
    private readonly IConversationResolver _resolver;
    private readonly IChatMessageWriter _writer;

    public PostReadReceiptHandler(IConversationResolver resolver, IChatMessageWriter writer)
    {
        _resolver = resolver;
        _writer = writer;
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
    }
}

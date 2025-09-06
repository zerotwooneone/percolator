using MediatR;
using Percolator.Chat.App;

namespace Percolator.Chat.App.Commands;

public sealed class PostDeliveredReceiptHandler : IRequestHandler<PostDeliveredReceiptCommand>
{
    private readonly IConversationResolver _resolver;
    private readonly IChatMessageWriter _writer;

    public PostDeliveredReceiptHandler(IConversationResolver resolver, IChatMessageWriter writer)
    {
        _resolver = resolver;
        _writer = writer;
    }

    public async Task Handle(PostDeliveredReceiptCommand request, CancellationToken cancellationToken)
    {
        request.LookupKey.EnsureExactlyOne();
        var resolution = await _resolver.ResolveAsync(request.LookupKey, cancellationToken);
        await _writer.AddDeliveredReceiptAsync(
            resolution.Conversation.Id,
            resolution.SelfIdentityId,
            request.MessageId,
            request.DeliveredAtUtc,
            cancellationToken);
    }
}

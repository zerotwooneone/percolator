using MediatR;
using Percolator.Chat.GroupMembership;
using Percolator.Chat.Messaging.App.Commands;
using Percolator.Chat.Messaging.Events;

namespace Percolator.Chat.Messaging.App.Handlers;

public sealed class ReceiveReadReceiptHandler : IRequestHandler<ReceiveReadReceiptCommand>
{
    private readonly IDirectConversationResolver _resolver;
    private readonly IChatMessageWriter _writer;

    public ReceiveReadReceiptHandler(
        IDirectConversationResolver resolver,
        IChatMessageWriter writer,
        IPublisher publisher)
    {
        _resolver = resolver;
        _writer = writer;
    }

    public async Task Handle(ReceiveReadReceiptCommand request, CancellationToken cancellationToken)
    {
        request.LookupKey.EnsureExactlyOne();
        var resolution = await _resolver.ResolveAsync(request.LookupKey, cancellationToken).ConfigureAwait(false);

        await _writer.AddReadReceiptAsync(
            resolution.Conversation.Id,
            resolution.SelfIdentityId,
            request.ReaderId,
            request.PublicMessageId,
            request.SentTimestampUtc,
            cancellationToken).ConfigureAwait(false);
    }
}

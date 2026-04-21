using MediatR;
using Percolator.Chat.App;
using Percolator.Chat.App.Commands;
using Percolator.Chat.Events;
using Percolator.Chat.ValueObjects;

namespace Percolator.Chat.App.Handlers;

public sealed class ReceiveDeliveredReceiptHandler : IRequestHandler<ReceiveDeliveredReceiptCommand>
{
    private readonly IConversationResolver _resolver;
    private readonly IChatMessageWriter _writer;
    private readonly IPublisher _publisher;

    public ReceiveDeliveredReceiptHandler(
        IConversationResolver resolver,
        IChatMessageWriter writer,
        IPublisher publisher)
    {
        _resolver = resolver;
        _writer = writer;
        _publisher = publisher;
    }

    public async Task Handle(ReceiveDeliveredReceiptCommand request, CancellationToken cancellationToken)
    {
        request.LookupKey.EnsureExactlyOne();
        var resolution = await _resolver.ResolveAsync(request.LookupKey, cancellationToken).ConfigureAwait(false);

        await _writer.AddDeliveredReceiptAsync(
            resolution.Conversation.Id,
            resolution.SelfIdentityId,
            request.RecipientId,
            request.MessageId,
            request.DeliveredTimestampUtc,
            cancellationToken).ConfigureAwait(false);

        await _publisher.Publish(new DeliveredReceiptReceivedEvent(
            resolution.Conversation.Id.Value,
            request.MessageId.Value,
            resolution.SelfIdentityId,
            request.RecipientId.Value,
            request.DeliveredTimestampUtc,
            DirectSessionIdValueObject.FromGuid(request.LookupKey.DirectSessionId)), cancellationToken).ConfigureAwait(false);
    }
}

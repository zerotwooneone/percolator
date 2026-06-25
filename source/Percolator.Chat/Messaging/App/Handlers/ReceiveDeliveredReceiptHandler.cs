using MediatR;
using Percolator.Chat.GroupMembership;
using Percolator.Chat.Messaging.App.Commands;
using Percolator.Chat.Messaging.Events;
using Percolator.Chat.Messaging.ValueObjects;

namespace Percolator.Chat.Messaging.App.Handlers;

public sealed class ReceiveDeliveredReceiptHandler : IRequestHandler<ReceiveDeliveredReceiptCommand>
{
    private readonly IDirectConversationResolver _resolver;
    private readonly IChatMessageWriter _writer;
    private readonly IPublisher _publisher;

    public ReceiveDeliveredReceiptHandler(
        IDirectConversationResolver resolver,
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
            new ChatPeerId(request.RecipientId.Value),
            request.DeliveredTimestampUtc,
            DirectSessionIdValueObject.FromGuid(request.LookupKey.DirectSessionId)), cancellationToken).ConfigureAwait(false);
    }
}

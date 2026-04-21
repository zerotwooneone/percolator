using MediatR;
using Percolator.Chat.Events;
using Percolator.Chat.ValueObjects;
using Percolator.Network;
using Desktop.Wpf.Features.Chat.State;

namespace Desktop.Wpf.Features.Chat.Handlers;

public sealed class DeliveredReceiptReceivedEventHandler : INotificationHandler<DeliveredReceiptReceivedEvent>
{
    private readonly ChatStateService _state;

    public DeliveredReceiptReceivedEventHandler(ChatStateService state)
    {
        _state = state;
    }

    public Task Handle(DeliveredReceiptReceivedEvent notification, CancellationToken cancellationToken)
    {
        if (notification.DirectSessionId is not { } dsid) return Task.CompletedTask;

        var sessionId = new DirectSessionId(dsid.Value);
        var messageId = new MessageId(notification.MessageId);

        // Mark as delivered via public method (handles locking internally)
        _state.MarkAsDelivered(sessionId, messageId);
        return Task.CompletedTask;
    }
}

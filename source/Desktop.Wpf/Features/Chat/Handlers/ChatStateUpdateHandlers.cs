using Desktop.Wpf.Features.Chat.State;
using MediatR;
using Percolator.Chat.Events;
using R3;

namespace Desktop.Wpf.Features.Chat.Handlers;

public sealed class ChatStateUpdateHandlers : 
    INotificationHandler<TextMessagePostedEvent>, 
    INotificationHandler<TextMessageReceivedEvent>
{
    private readonly ChatStateService _state;
    private readonly ChatReloadCoordinator _reload;

    public ChatStateUpdateHandlers(ChatStateService state, ChatReloadCoordinator reload)
    {
        _state = state;
        _reload = reload;
    }

    public Task Handle(TextMessagePostedEvent notification, CancellationToken cancellationToken)
    {
        // Trigger reload for the conversation to get the full history including the new message
        _reload.TriggerReloadForSession(notification.ConversationId.ToString());
        return Task.CompletedTask;
    }

    public Task Handle(TextMessageReceivedEvent notification, CancellationToken cancellationToken)
    {
        // Trigger reload for the conversation to get the full history including the new message
        _reload.TriggerReloadForSession(notification.ConversationId.ToString());
        return Task.CompletedTask;
    }
}

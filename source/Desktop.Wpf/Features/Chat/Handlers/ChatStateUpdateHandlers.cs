using Desktop.Wpf.Features.Chat.State;
using MediatR;
using Percolator.Chat.Events;
using Percolator.Chat.ValueObjects;
using R3;

namespace Desktop.Wpf.Features.Chat.Handlers;

public sealed class ChatStateUpdateHandlers : 
    INotificationHandler<TextMessagePostedEvent>, 
    INotificationHandler<TextMessageReceivedEvent>
{
    private readonly IChatReloadCoordinator _reload;

    public ChatStateUpdateHandlers(IChatReloadCoordinator reload)
    {
        _reload = reload;
    }

    public Task Handle(TextMessagePostedEvent notification, CancellationToken cancellationToken)
    {
        // Trigger reload for the conversation to get the full history including the new message
        _reload.TriggerReloadForConversation(new ConversationId(notification.ConversationId), notification.SenderSelfIdentityId);
        return Task.CompletedTask;
    }

    public Task Handle(TextMessageReceivedEvent notification, CancellationToken cancellationToken)
    {
        // Trigger reload for the conversation to get the full history including the new message
        _reload.TriggerReloadForConversation(new ConversationId(notification.ConversationId), notification.SelfIdentityId);
        return Task.CompletedTask;
    }
}

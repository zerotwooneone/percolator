using System;
using System.Threading;
using System.Threading.Tasks;
using MediatR;
using Percolator.Application.Network;

namespace Desktop.Wpf.Features.Sessions;

public sealed class PendingHandshakeEventListener :
    INotificationHandler<PendingSessionCreatedNotification>
{
    private readonly ISecureChannelsListEvents _events;

    public PendingHandshakeEventListener(
        ISecureChannelsListEvents events)
    {
        _events = events;
    }

    public Task Handle(PendingSessionCreatedNotification notification, CancellationToken cancellationToken)
    {
        // IMPORTANT: This handler can run in a background DI scope, so it must not mutate
        // scoped ViewModel instances directly. Notify a singleton event instead.
        _events.NotifyChanged();
        return Task.CompletedTask;
    }
}

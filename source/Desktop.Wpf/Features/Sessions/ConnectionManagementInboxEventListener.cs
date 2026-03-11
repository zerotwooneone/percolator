using System.Threading;
using System.Threading.Tasks;
using MediatR;
using Percolator.Application.Network;

namespace Desktop.Wpf.Features.Sessions;

public sealed class ConnectionManagementInboxEventListener : INotificationHandler<PendingSessionCreatedNotification>
{
    private readonly IMainInvitationInboxEvents _events;

    public ConnectionManagementInboxEventListener(IMainInvitationInboxEvents events)
    {
        _events = events;
    }

    public Task Handle(PendingSessionCreatedNotification notification, CancellationToken cancellationToken)
    {
        _events.NotifyChanged();
        return Task.CompletedTask;
    }
}

using MediatR;
using Percolator.Application.Network;

namespace Desktop.Wpf.Features.Sessions.Handlers;

public sealed class PeerConnectionStateUpdateHandlers :
    INotificationHandler<SecureSessionCreatedNotification>,
    INotificationHandler<PendingSessionCreatedNotification>,
    INotificationHandler<PendingSessionRemovedNotification>,
    INotificationHandler<SentInvitationUpsertedNotification>
{
    private readonly PeerConnectionReloadCoordinator _coordinator;

    public PeerConnectionStateUpdateHandlers(PeerConnectionReloadCoordinator coordinator)
    {
        _coordinator = coordinator;
    }

    public Task Handle(SecureSessionCreatedNotification notification, CancellationToken cancellationToken)
    {
        _coordinator.TriggerReload();
        return Task.CompletedTask;
    }

    public Task Handle(PendingSessionCreatedNotification notification, CancellationToken cancellationToken)
    {
        _coordinator.TriggerReload();
        return Task.CompletedTask;
    }

    public Task Handle(PendingSessionRemovedNotification notification, CancellationToken cancellationToken)
    {
        _coordinator.TriggerReload();
        return Task.CompletedTask;
    }

    public Task Handle(SentInvitationUpsertedNotification notification, CancellationToken cancellationToken)
    {
        _coordinator.TriggerReload();
        return Task.CompletedTask;
    }
}

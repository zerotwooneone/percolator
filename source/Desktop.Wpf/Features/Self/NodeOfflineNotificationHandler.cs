using MediatR;
using Microsoft.Extensions.Logging;
using Percolator.Application.Network;
using System.Windows;

namespace Desktop.Wpf.Features.Self;

public class NodeOfflineNotificationHandler : INotificationHandler<NodeOfflineNotification>
{
    private readonly ILogger<NodeOfflineNotificationHandler> _logger;

    public NodeOfflineNotificationHandler(ILogger<NodeOfflineNotificationHandler> logger)
    {
        _logger = logger;
    }

    public Task Handle(NodeOfflineNotification notification, CancellationToken ct)
    {
        _logger.LogError("Node offline notification received for identity {IdentityId}: {Reason}",
            notification.IdentityId.Value, notification.Reason);

        // Show UI alert on the UI thread
        Application.Current.Dispatcher.Invoke(() =>
        {
            MessageBox.Show(
                $"Your identity is offline: {notification.Reason}",
                "Network Error",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        });

        return Task.CompletedTask;
    }
}

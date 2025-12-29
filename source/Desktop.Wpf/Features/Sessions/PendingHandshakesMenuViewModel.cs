using System.Collections.ObjectModel;
using System.Threading.Tasks;
using Desktop.Wpf.Shared.Mvvm;
using MediatR;
using Percolator.Application.Cryptography;
using Percolator.Application.Network;
using Percolator.Cryptography;

namespace Desktop.Wpf.Features.Sessions;

public sealed class PendingHandshakeItem
{
    public required string DisplayName { get; init; } 
    public required string Initials { get; set; }
    public string BundleText { get; set; } = "Not Set";
    public required PendingSessionId PendingId { get; init; }

    public string StatusText { get; set; } = "Pending";
    public string? SendPath { get; set; }
    public string? RequestCorrelationId { get; set; }
    public bool IsExpired { get; set; }

    public bool IsRelayed { get; set; }
    public string? RelayInfoText { get; set; }
}

public sealed class PendingHandshakesMenuViewModel
{
    public ObservableCollection<PendingHandshakeItem> PendingHandshakes { get; } = new();

    public AsyncRelayCommand AcceptHandshakeCommand { get; }
    public AsyncRelayCommand BurnHandshakeCommand { get; }

    public PendingHandshakesMenuViewModel(
        IMediator mediator,
        IPendingSessionRepository pendingSessions)
    {
        AcceptHandshakeCommand = new AsyncRelayCommand(async obj =>
        {
            if (obj is PendingHandshakeItem item)
            {
                var result = await mediator.Send(new ApprovePendingSessionCommand(item.PendingId)).ConfigureAwait(false);
                switch (result)
                {
                    case ApprovePendingSessionResult.Accepted accepted:
                        item.StatusText = "Accepted";
                        item.SendPath = accepted.SendPath;
                        item.RequestCorrelationId = accepted.RequestCorrelationId.Value.ToString();
                        item.IsExpired = false;
                        break;
                    case ApprovePendingSessionResult.RejectedNotReady:
                        item.StatusText = "Rejected: Not Ready";
                        break;
                    case ApprovePendingSessionResult.RejectedInvalid:
                        item.StatusText = "Rejected: Invalid";
                        break;
                    case ApprovePendingSessionResult.RejectedExpired:
                        item.StatusText = "Rejected: Expired";
                        item.IsExpired = true;
                        break;
                    case ApprovePendingSessionResult.Failed failed:
                        item.StatusText = $"Failed: {failed.ErrorMessage}";
                        break;
                    default:
                        item.StatusText = "Failed: Unknown";
                        break;
                }
            }
        });
        BurnHandshakeCommand = new AsyncRelayCommand(async obj =>
        {
            if (obj is PendingHandshakeItem item)
            {
                var pending = await pendingSessions.GetAsync(item.PendingId);
                if (pending is null)
                {
                    PendingHandshakes.Remove(item);
                    return;
                }
                pending.Reject();
                pendingSessions.UpdateAsync(pending);
                PendingHandshakes.Remove(item);
            }
        });
    }
}

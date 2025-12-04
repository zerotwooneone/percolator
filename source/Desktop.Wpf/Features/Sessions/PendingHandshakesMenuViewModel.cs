using System.Collections.ObjectModel;
using System.Threading.Tasks;
using Desktop.Wpf.Shared.Mvvm;
using Percolator.Application.Cryptography;
using Percolator.Cryptography;

namespace Desktop.Wpf.Features.Sessions;

public sealed class PendingHandshakeItem
{
    public required string DisplayName { get; init; } 
    public required string Initials { get; set; }
    public string BundleText { get; set; } = "Not Set";
    public required PendingSessionId PendingId { get; init; }
}

public sealed class PendingHandshakesMenuViewModel
{
    public ObservableCollection<PendingHandshakeItem> PendingHandshakes { get; } = new();

    public AsyncRelayCommand AcceptHandshakeCommand { get; }
    public AsyncRelayCommand BurnHandshakeCommand { get; }

    public PendingHandshakesMenuViewModel(IPendingSessionRepository pendingSessions)
    {
        AcceptHandshakeCommand = new AsyncRelayCommand(async obj =>
        {
            if (obj is PendingHandshakeItem item)
            {
                var pending = await pendingSessions.GetAsync(item.PendingId);
                if (pending is null)
                {
                    PendingHandshakes.Remove(item);
                    return;
                }
                //todo:generate response and send
                PendingHandshakes.Remove(item);
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

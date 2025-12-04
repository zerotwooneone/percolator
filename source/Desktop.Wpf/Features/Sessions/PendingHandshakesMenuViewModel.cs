using System.Collections.ObjectModel;
using System.Threading.Tasks;
using Desktop.Wpf.Shared.Mvvm;
using Percolator.Cryptography;

namespace Desktop.Wpf.Features.Sessions;

public sealed class PendingHandshakeItem
{
    public string DisplayName { get; set; } = "Unknown";
    public string Initials { get; set; } = "UK";
    public string BundleText { get; set; } = "Not Set";
    public PendingSessionId PendingId { get; set; }
}

public sealed class PendingHandshakesMenuViewModel
{
    public ObservableCollection<PendingHandshakeItem> PendingHandshakes { get; } = new();

    public AsyncRelayCommand AcceptHandshakeCommand { get; }
    public AsyncRelayCommand BurnHandshakeCommand { get; }

    public PendingHandshakesMenuViewModel()
    {
        AcceptHandshakeCommand = new AsyncRelayCommand(async obj =>
        {
            if (obj is PendingHandshakeItem item)
            {
                // TODO: hook into application service to accept
                PendingHandshakes.Remove(item);
            }
        });
        BurnHandshakeCommand = new AsyncRelayCommand(async obj =>
        {
            if (obj is PendingHandshakeItem item)
            {
                // TODO: hook into application service to burn
                PendingHandshakes.Remove(item);
            }
        });
    }
}

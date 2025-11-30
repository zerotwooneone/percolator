using System.Collections.ObjectModel;
using System.Threading.Tasks;
using Desktop.Wpf.Shared.Mvvm;

namespace Desktop.Wpf.Features.Sessions;

public sealed class PendingHandshakeItem
{
    public string DisplayName { get; set; } = "Anon_User_402";
    public string Initials { get; set; } = "AU";
    public string BundleText { get; set; } = "Bundle: PK-NBM094";
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
                await Task.CompletedTask;
                PendingHandshakes.Remove(item);
            }
        });
        BurnHandshakeCommand = new AsyncRelayCommand(async obj =>
        {
            if (obj is PendingHandshakeItem item)
            {
                // TODO: hook into application service to burn
                await Task.CompletedTask;
                PendingHandshakes.Remove(item);
            }
        });
    }
}

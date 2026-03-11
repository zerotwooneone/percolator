using Desktop.Wpf.Shared.Mvvm;
using R3;
using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace Desktop.Wpf.Features.Sessions;

public sealed class ConnectionManagementDialogViewModel : ViewModelBase
{
    private readonly DisposableBag _bag;

    private readonly IMainInvitationInbox _inbox;

    public BindableReactiveProperty<int> SelectedTabIndex { get; }

    public ConnectionManagementDialogViewModel(IMainInvitationInbox inbox)
    {
        _inbox = inbox;
        SelectedTabIndex = new BindableReactiveProperty<int>(0).AddTo(ref _bag);

        _ = InitializeAsync();
    }

    private async Task InitializeAsync(CancellationToken ct = default)
    {
        try
        {
            var open = await _inbox.GetOpenAsync(ct).ConfigureAwait(false);
            var desired = open.Count > 0 ? 0 : 1;

            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher is null || dispatcher.CheckAccess())
            {
                SelectedTabIndex.Value = desired;
            }
            else
            {
                await dispatcher.InvokeAsync(() => SelectedTabIndex.Value = desired);
            }
        }
        catch
        {
            // Leave default tab index.
        }
    }

    protected override void DisposeCore()
    {
        Disposable.Dispose(SelectedTabIndex);
        _bag.Dispose();
    }
}

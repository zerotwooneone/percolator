using Desktop.Wpf.Shared.Mvvm;
using R3;

namespace Desktop.Wpf.Features.Sessions;

public sealed class ConnectionManagementDialogViewModel : ViewModelBase
{
    private readonly DisposableBag _bag;

    public BindableReactiveProperty<int> SelectedTabIndex { get; }

    public ConnectionManagementDialogViewModel()
    {
        SelectedTabIndex = new BindableReactiveProperty<int>(0).AddTo(ref _bag);
    }

    protected override void DisposeCore()
    {
        Disposable.Dispose(SelectedTabIndex);
        _bag.Dispose();
    }
}

using Desktop.Wpf.Shared.Mvvm;
using R3;

namespace Desktop.Wpf.Features.Sessions;

public interface ISessionConductor
{
    void Show(object? viewModel);
}

public sealed class SessionShellViewModel : ViewModelBase, ISessionConductor
{
    public object? Sidebar { get; set; }
    public object? RightPane { get; set; }
    public BindableReactiveProperty<object?> CurrentContent { get; } = new(null);

    public void Show(object? viewModel)
    {
        CurrentContent.Value = viewModel;
    }

    protected override void DisposeCore()
    {
        Disposable.Dispose(CurrentContent);
    }
}

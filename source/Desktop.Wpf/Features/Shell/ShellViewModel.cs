using System;
using R3;
using Desktop.Wpf.Shared.Navigation;

namespace Desktop.Wpf.Features.Shell;

public sealed class ShellViewModel : ViewModelBase
{
    public IReadOnlyBindableReactiveProperty<object?> CurrentView { get; }

    public ShellViewModel(INavigationService navigation)
    {
        // Bind navigation stream to a bindable read-only property for ContentControl binding later
        CurrentView = navigation.ViewStream
            .ObserveOnCurrentSynchronizationContext()
            .ToReadOnlyBindableReactiveProperty<object?>(null);
    }

    protected override void DisposeCore()
    {
        Disposable.Dispose(CurrentView);
    }
}

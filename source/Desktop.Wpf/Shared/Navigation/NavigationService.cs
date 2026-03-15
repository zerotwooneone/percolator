using R3;

namespace Desktop.Wpf.Shared.Navigation;

public sealed class NavigationService : INavigationService
{
    private readonly ReactiveProperty<object?> _current = new(null);

    public Observable<object?> ViewStream => _current;

    public void Navigate(object? view)
    {
        _current.OnNext(view);
        System.Diagnostics.Debug.WriteLine($"[Nav] Navigate -> {view?.GetType().Name ?? "null"}");
    }
}

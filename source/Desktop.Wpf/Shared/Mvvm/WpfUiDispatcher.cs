using System.Windows;

namespace Desktop.Wpf.Shared.Mvvm;

public sealed class WpfUiDispatcher : IUiDispatcher
{
    public bool CheckAccess()
        => Application.Current?.Dispatcher?.CheckAccess() ?? true;

    public Task InvokeAsync(Action action, CancellationToken cancellationToken = default)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            action();
            return Task.CompletedTask;
        }

        return dispatcher.InvokeAsync(action, System.Windows.Threading.DispatcherPriority.Normal, cancellationToken).Task;
    }

    public Task<T> InvokeAsync<T>(Func<T> func, CancellationToken cancellationToken = default)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            return Task.FromResult(func());
        }

        return dispatcher.InvokeAsync(func, System.Windows.Threading.DispatcherPriority.Normal, cancellationToken).Task;
    }
}

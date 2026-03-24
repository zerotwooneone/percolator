namespace Desktop.Wpf.Shared.Mvvm;

public interface IUiDispatcher
{
    bool CheckAccess();
    Task InvokeAsync(Action action, CancellationToken cancellationToken = default);
    Task<T> InvokeAsync<T>(Func<T> func, CancellationToken cancellationToken = default);
}

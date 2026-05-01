using System.Windows.Input;
using R3;

namespace Desktop.Wpf.Shared.Mvvm;

public sealed class ReactiveAsyncCommand  : ICommand, IDisposable
{
    private readonly IUiDispatcher _ui;
    private readonly Func<object?, Task> _execute;
    private readonly Func<object?, bool>? _canExecute;
    private readonly IDisposable? _requerySubscription;
    private bool _executing;
    private bool _disposed;

    public ReactiveAsyncCommand(
        IUiDispatcher ui,
        Func<object?, Task> execute,
        Observable<Unit> requery,
        Func<object?, bool>? canExecute = null)
    {
        _ui = ui ?? throw new ArgumentNullException(nameof(ui));
        _execute = execute ?? throw new ArgumentNullException(nameof(execute));
        _canExecute = canExecute;

        _requerySubscription = requery.Subscribe(_ => RaiseCanExecuteChanged());
    }

    public ReactiveAsyncCommand(
        IUiDispatcher ui,
        Func<Task> execute,
        Observable<Unit> requery,
        Func<bool>? canExecute = null)
        : this(
            ui,
            _ => execute(),
            requery,
            canExecute is null ? null : (_ => canExecute()))
    {
    }

    public bool CanExecute(object? parameter)
        => !_disposed && !_executing && (_canExecute?.Invoke(parameter) ?? true);

    public async void Execute(object? parameter)
    {
        if (!CanExecute(parameter)) return;

        try
        {
            _executing = true;
            RaiseCanExecuteChanged();
            await _execute(parameter).ConfigureAwait(false);
        }
        finally
        {
            _executing = false;
            RaiseCanExecuteChanged();
        }
    }

    public event EventHandler? CanExecuteChanged;

    public void RaiseCanExecuteChanged()
    {
        if (_disposed) return;

        if (_ui.CheckAccess())
        {
            CanExecuteChanged?.Invoke(this, EventArgs.Empty);
            return;
        }

        _ = _ui.InvokeAsync(() =>
        {
            if (_disposed) return;
            CanExecuteChanged?.Invoke(this, EventArgs.Empty);
        });
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _requerySubscription?.Dispose();
    }
}

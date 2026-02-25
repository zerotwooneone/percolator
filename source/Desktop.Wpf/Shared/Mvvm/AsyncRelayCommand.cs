using System;
using System.Threading.Tasks;
using System.Windows.Input;

namespace Desktop.Wpf.Shared.Mvvm;

public sealed class AsyncRelayCommand : ICommand
{
    private readonly Func<object?, Task> _execute;
    private readonly Func<object?, bool>? _canExecute;
    private bool _executing;
    private readonly int _debounceMs;
    private int _debounceVersion;

    public AsyncRelayCommand(Func<object?, Task> execute, Func<object?, bool>? canExecute = null)
    {
        _execute = execute;
        _canExecute = canExecute;
        _debounceMs = 0;
    }

    public AsyncRelayCommand(int debounceMs, Func<object?, Task> execute, Func<object?, bool>? canExecute = null)
    {
        _execute = execute;
        _canExecute = canExecute;
        _debounceMs = debounceMs;
    }

    public bool CanExecute(object? parameter)
        => !_executing && (_canExecute?.Invoke(parameter) ?? true);

    public async void Execute(object? parameter)
    {
        if (!CanExecute(parameter)) return;

        if (_debounceMs > 0)
        {
            var myVersion = System.Threading.Interlocked.Increment(ref _debounceVersion);
            try
            {
                await Task.Delay(_debounceMs).ConfigureAwait(false);
            }
            catch
            {
                return;
            }

            if (myVersion != _debounceVersion)
            {
                return;
            }
        }

        try
        {
            _executing = true;
            RaiseCanExecuteChanged();
            await _execute(parameter);
        }
        finally
        {
            _executing = false;
            RaiseCanExecuteChanged();
        }
    }

    public event EventHandler? CanExecuteChanged;
    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

using System.Windows.Input;

namespace Percolator.Desktop.Main;

public class BaseCommand : ICommand
{
    private readonly Predicate<object?>? canExecute;
    private readonly Action<object?> action;

    public BaseCommand(Action<object?> action, Predicate<object?>? canExecute = null)
    {
        this.action = action ?? throw new ArgumentNullException(nameof(action));
        this.canExecute = canExecute;
    }

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter)
    {
        return canExecute == null || canExecute(parameter);
    }

    public void Execute(object? parameter)
    {
        action(parameter);
    }

    public void RaiseCanExecuteChanged()
    {
        CanExecuteChanged?.Invoke(this, EventArgs.Empty);
    }
}
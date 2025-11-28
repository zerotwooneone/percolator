using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Desktop.Wpf.Shared.Navigation;

public sealed class NavigationService : INavigationService
{
    private object? _currentView;
    public object? CurrentView
    {
        get => _currentView;
        private set
        {
            if (!ReferenceEquals(_currentView, value))
            {
                _currentView = value;
                OnPropertyChanged();
            }
        }
    }

    public void Navigate(object? view)
    {
        CurrentView = view;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

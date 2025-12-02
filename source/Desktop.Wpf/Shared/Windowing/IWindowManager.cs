using System.Windows;

namespace Desktop.Wpf.Shared.Windowing;

public interface IWindowManager
{
    bool TryActivate<TWindow>() where TWindow : Window;
    bool Show<TWindow>() where TWindow : Window;

    bool ShowFor<TViewModel>() where TViewModel : class;
}

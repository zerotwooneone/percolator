using System.Windows;

namespace Desktop.Wpf.Shared.Windowing;

public interface IWindowManager
{
    bool Show<TWindow>() where TWindow : Window;
    bool TryActivate<TWindow>() where TWindow : Window;
}

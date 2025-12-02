using System;
using System.Collections.Concurrent;
using System.Windows;

namespace Desktop.Wpf.Shared.Windowing;

public interface IWindowViewRegistry
{
    void Register<TViewModel, TWindow>() where TViewModel : class where TWindow : Window;
    void Register(Type viewModelType, Type windowType);
    bool TryGetWindowType(Type viewModelType, out Type windowType);
}

public sealed class WindowViewRegistry : IWindowViewRegistry
{
    private readonly ConcurrentDictionary<Type, Type> _map = new();

    public void Register<TViewModel, TWindow>()
        where TViewModel : class
        where TWindow : Window
    {
        _map[typeof(TViewModel)] = typeof(TWindow);
    }

    public void Register(Type viewModelType, Type windowType)
    {
        if (viewModelType is null) throw new ArgumentNullException(nameof(viewModelType));
        if (windowType is null) throw new ArgumentNullException(nameof(windowType));
        if (!typeof(Window).IsAssignableFrom(windowType)) throw new ArgumentException("windowType must be a WPF Window type", nameof(windowType));
        _map[viewModelType] = windowType;
    }

    public bool TryGetWindowType(Type viewModelType, out Type windowType)
    {
        return _map.TryGetValue(viewModelType, out windowType!);
    }
}

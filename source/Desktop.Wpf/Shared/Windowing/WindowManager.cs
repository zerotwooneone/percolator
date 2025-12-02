using System;
using System.Collections.Concurrent;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Desktop.Wpf.Features.Shell;

namespace Desktop.Wpf.Shared.Windowing;

public sealed class WindowManager : IWindowManager
{
    private readonly IIdentityScopeAccessor _identityScopeAccessor;
    private readonly ConcurrentDictionary<Type, WeakReference<Window>> _open = new();

    public WindowManager(IIdentityScopeAccessor identityScopeAccessor)
    {
        _identityScopeAccessor = identityScopeAccessor;
    }

    public bool TryActivate<TWindow>() where TWindow : Window
    {
        if (_open.TryGetValue(typeof(TWindow), out var wr) && wr.TryGetTarget(out var win) && win.IsVisible)
        {
            win.Activate();
            win.Focus();
            return true;
        }
        return false;
    }

    public bool Show<TWindow>() where TWindow : Window
    {
        if (TryActivate<TWindow>()) return true;

        var provider = _identityScopeAccessor.Current;
        if (provider is null) return false; // Identity not ready yet

        using var scope = provider.GetRequiredService<IServiceScopeFactory>().CreateScope();
        var window = scope.ServiceProvider.GetRequiredService<TWindow>();
        // Track and clean up when closed
        window.Closed += (_, __) =>
        {
            WeakReference<Window>? removed;
            _open.TryRemove(typeof(TWindow), out removed);
        };
        _open[typeof(TWindow)] = new WeakReference<Window>(window);
        if (Application.Current is { MainWindow: { } owner })
            window.Owner = owner;
        window.Show();
        window.Activate();
        return true;
    }
}

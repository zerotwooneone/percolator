using System;
using System.Collections.Concurrent;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Desktop.Wpf.Features.Shell;

namespace Desktop.Wpf.Shared.Windowing;

public sealed class WindowManager : IWindowManager
{
    private readonly IIdentityScopeAccessor _identityScopeAccessor;
    private readonly IWindowViewRegistry _registry;

    private sealed record OpenWindowEntry(WeakReference<Window> WindowRef, IServiceScope Scope);

    private readonly ConcurrentDictionary<Type, OpenWindowEntry> _open = new();

    public WindowManager(IIdentityScopeAccessor identityScopeAccessor, IWindowViewRegistry registry)
    {
        _identityScopeAccessor = identityScopeAccessor;
        _registry = registry;
    }

    public bool TryActivate<TWindow>() where TWindow : Window
    {
        if (_open.TryGetValue(typeof(TWindow), out var entry)
            && entry.WindowRef.TryGetTarget(out var win)
            && win.IsVisible)
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

        // IMPORTANT: Windows are registered Scoped; resolving from the identity scope would reuse a closed window.
        // Create a per-window child scope and dispose it when the window closes.
        var scope = provider.CreateScope();
        var window = scope.ServiceProvider.GetRequiredService<TWindow>();

        window.Closed += (_, __) =>
        {
            if (_open.TryRemove(typeof(TWindow), out var removed))
            {
                try { removed.Scope.Dispose(); } catch { }
            }
            else
            {
                try { scope.Dispose(); } catch { }
            }
        };

        _open[typeof(TWindow)] = new OpenWindowEntry(new WeakReference<Window>(window), scope);
        if (Application.Current is { MainWindow: { } owner })
            window.Owner = owner;
        window.Show();
        window.Activate();
        return true;
    }

    public bool ShowFor<TViewModel>() where TViewModel : class
    {
        var provider = _identityScopeAccessor.Current;
        if (provider is null)
            throw new InvalidOperationException("Identity scope is not available. Ensure Shell has initialized and set IIdentityScopeAccessor.Current.");

        var vmType = typeof(TViewModel);
        if (!_registry.TryGetWindowType(vmType, out var windowType))
            throw new InvalidOperationException($"No window mapping registered for ViewModel type {vmType.FullName}. Add an entry in ViewMappings.xaml or register programmatically.");

        // Try to activate existing window
        if (_open.TryGetValue(windowType, out var existingEntry)
            && existingEntry.WindowRef.TryGetTarget(out var existing)
            && existing.IsVisible)
        {
            existing.Activate();
            existing.Focus();
            return true;
        }

        // If the window object is still alive but closed/unloaded, it's not showable again.
        if (_open.TryGetValue(windowType, out var staleEntry)
            && staleEntry.WindowRef.TryGetTarget(out var stale)
            && !stale.IsVisible
            && !stale.IsLoaded)
        {
            if (_open.TryRemove(windowType, out var removed))
            {
                try { removed.Scope.Dispose(); } catch { }
            }
        }

        // Use a per-window child scope to avoid reusing scoped VMs/windows after close.
        var scope = provider.CreateScope();

        var vm = scope.ServiceProvider.GetRequiredService<TViewModel>();
        if (vm is null)
            throw new InvalidOperationException($"Failed to resolve ViewModel {vmType.FullName} from the identity scope.");

        var windowObj = scope.ServiceProvider.GetRequiredService(windowType);
        if (windowObj is not Window window)
            throw new InvalidOperationException($"Resolved object for {windowType.FullName} is not a Window.");

        if (window.DataContext is null)
            window.DataContext = vm;

        window.Closed += (_, __) =>
        {
            if (_open.TryRemove(windowType, out var removed))
            {
                try { removed.Scope.Dispose(); } catch { }
            }
            else
            {
                try { scope.Dispose(); } catch { }
            }
        };

        _open[windowType] = new OpenWindowEntry(new WeakReference<Window>(window), scope);
        if (Application.Current is { MainWindow: { } owner })
            window.Owner = owner;
        window.Show();
        window.Activate();
        return true;
    }
}

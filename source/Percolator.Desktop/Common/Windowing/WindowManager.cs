using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Percolator.Desktop.ViewModels;

namespace Percolator.Desktop.Common.Windowing;

public sealed class WindowManager : IWindowManager
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<WindowManager> _logger;
    private readonly Dictionary<Type, (Window Window, IServiceScope Scope)> _openWindows = new();

    public WindowManager(IServiceProvider serviceProvider, ILogger<WindowManager> logger)
    {
        _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    private static IClassicDesktopStyleApplicationLifetime? DesktopLifetime =>
        Avalonia.Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime;

    public void Show<TViewModel>() where TViewModel : ViewModelBase
    {
        var vmType = typeof(TViewModel);

        lock (_openWindows)
        {
            if (_openWindows.TryGetValue(vmType, out var existing))
            {
                _logger.LogDebug("Window for {ViewModelType} is already open. Bringing to foreground.", vmType.Name);
                existing.Window.Activate();
                return;
            }

            var scope = _serviceProvider.CreateScope();
            try
            {
                var viewModel = scope.ServiceProvider.GetRequiredService<TViewModel>();
                var window = ResolveWindowByConvention(viewModel);

                window.DataContext = viewModel;
                _openWindows[vmType] = (window, scope);

                window.Closed += (_, _) =>
                {
                    lock (_openWindows)
                    {
                        _openWindows.Remove(vmType);
                    }

                    _logger.LogDebug("Window for {ViewModelType} closed. Disposing child DI scope.", vmType.Name);
                    scope.Dispose();
                };

                window.Show();
            }
            catch (Exception ex)
            {
                scope.Dispose();
                _logger.LogError(ex, "Failed to spawn window for {ViewModelType}", vmType.Name);
                throw;
            }
        }
    }

    public async Task<TResult?> ShowDialogAsync<TViewModel, TResult>(
        Action<TViewModel>? configure = null, Window? owner = null) where TViewModel : ViewModelBase
    {
        var desktop = DesktopLifetime;
        var parent = owner ?? desktop?.MainWindow ?? GetActiveWindow();
        if (parent == null)
        {
            throw new InvalidOperationException("Cannot show dialog without an active parent window.");
        }

        using var scope = _serviceProvider.CreateScope();
        var viewModel = scope.ServiceProvider.GetRequiredService<TViewModel>();
        configure?.Invoke(viewModel);
        var window = ResolveWindowByConvention(viewModel);
        window.DataContext = viewModel;

        if (window is IDialogWindow<TResult> dialogWindow)
        {
            await window.ShowDialog(parent);
            return dialogWindow.DialogResult;
        }

        await window.ShowDialog(parent);
        return default;
    }

    public Window? GetActiveWindow()
    {
        var desktop = DesktopLifetime;
        return desktop?.Windows.FirstOrDefault(w => w.IsActive) ?? desktop?.MainWindow;
    }

    private static Window ResolveWindowByConvention(object viewModel)
    {
        var vmType = viewModel.GetType();

        // Convention: Features.X.ViewModels.FooViewModel -> Features.X.Views.FooWindow or FooView.
        // Transform only the class-name suffix — a blanket Replace on the full name corrupts
        // the "ViewModels" namespace segment into a nonexistent "Windows" namespace.
        var baseName = vmType.Name.EndsWith("ViewModel", StringComparison.Ordinal)
            ? vmType.Name[..^"ViewModel".Length]
            : vmType.Name;
        var viewNamespace = (vmType.Namespace ?? string.Empty).Replace("ViewModels", "Views");

        foreach (var candidate in new[]
                 {
                     $"{viewNamespace}.{baseName}Window",
                     $"{viewNamespace}.{baseName}View"
                 })
        {
            var windowType = Type.GetType(candidate) ?? vmType.Assembly.GetType(candidate);
            if (windowType is not null && typeof(Window).IsAssignableFrom(windowType))
            {
                return (Window)Activator.CreateInstance(windowType)!;
            }
        }

        throw new InvalidOperationException($"Could not locate a Window type matching ViewModel {vmType.FullName}");
    }
}

public interface IDialogWindow<out TResult>
{
    TResult? DialogResult { get; }
}

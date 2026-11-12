using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using Percolator.Desktop.ViewModels;

namespace Percolator.Desktop.Common.Windowing;

/// <summary>
/// Manages window lifecycle, single-instance secondary window focus, and child DI container isolation.
/// </summary>
public interface IWindowManager
{
    /// <summary>
    /// Shows a window for the given ViewModel type. If an instance is already open, focuses and brings it to the foreground.
    /// Spawns the window and ViewModel inside an isolated child IServiceScope that is disposed upon window closure.
    /// </summary>
    void Show<TViewModel>() where TViewModel : ViewModelBase;

    /// <summary>
    /// Shows a modal dialog for the given ViewModel, returning the dialog result upon closure.
    /// </summary>
    /// <param name="configure">Optional initializer invoked after the ViewModel resolves but before the window opens.</param>
    /// <param name="owner">Optional explicit owner; defaults to the active window.</param>
    Task<TResult?> ShowDialogAsync<TViewModel, TResult>(
        Action<TViewModel>? configure = null, Window? owner = null) where TViewModel : ViewModelBase;

    /// <summary>
    /// Returns the currently active top-level window.
    /// </summary>
    Window? GetActiveWindow();
}

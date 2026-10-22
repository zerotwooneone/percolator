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
    Task<TResult?> ShowDialogAsync<TViewModel, TResult>(Window? owner = null) where TViewModel : ViewModelBase;

    /// <summary>
    /// Switches the application shell from the Identity Gate to the main application window (MainWindow).
    /// </summary>
    void SwitchToMainWindow();

    /// <summary>
    /// Switches the application shell back to the Identity Gate (e.g. on session lock or logout),
    /// closing the main window and returning to stealth mode.
    /// </summary>
    void SwitchToIdentityGate();

    /// <summary>
    /// Returns the currently active top-level window.
    /// </summary>
    Window? GetActiveWindow();
}

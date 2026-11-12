// SPECIFICATION GOVERNANCE: Read Percolator.Desktop/spec/ViewModel.spec.md before modifying this file.

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Percolator.Desktop.ViewModels;

namespace Percolator.Desktop.Features.Shell.ViewModels;

/// <summary>
/// ViewModel for the DESK-01 responsive 3-pane shell frame. Owns transient shell-view state
/// (drawer open/closed, pending-request indicator). Conversation, transport, and network
/// state arrive in later chunks via stateful UI services.
/// </summary>
public partial class MainWindowViewModel : ViewModelBase
{
    /// <summary>
    /// Two-way bound to the right-anchored inspector <c>SplitView.IsPaneOpen</c>.
    /// </summary>
    [ObservableProperty]
    private bool _isInspectorOpen;

    /// <summary>
    /// Two-way bound to the right sidebar <c>SplitView.IsPaneOpen</c>. The code-behind
    /// switches the pane between inline and overlay display modes at the 850px breakpoint.
    /// </summary>
    [ObservableProperty]
    private bool _isChatsSidebarOpen = true;

    /// <summary>
    /// Count of staged inbound handshake requests awaiting pull-based review
    /// (drives the Left Sidebar pending-requests indicator light).
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPendingRequests))]
    private int _pendingRequestCount;

    /// <summary>True when staged inbound requests exist; shows the indicator light.</summary>
    public bool HasPendingRequests => PendingRequestCount > 0;

    /// <summary>Header title for the active conversation; empty when none is selected.</summary>
    [ObservableProperty]
    private string _activeConversationTitle = string.Empty;

    [RelayCommand]
    private void ToggleInspector()
    {
        IsInspectorOpen = !IsInspectorOpen;
    }

    [RelayCommand]
    private void ToggleChatsSidebar()
    {
        IsChatsSidebarOpen = !IsChatsSidebarOpen;
    }
}

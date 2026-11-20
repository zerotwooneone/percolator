// SPECIFICATION GOVERNANCE: Read Percolator.Desktop/spec/ViewModel.spec.md before modifying this file.

namespace Percolator.Desktop.Features.Shell.ViewModels;

using System;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Percolator.Desktop.Common.Services;
using Percolator.Desktop.Features.Chats.ViewModels;
using Percolator.Desktop.Features.Shell.Models;
using Percolator.Desktop.Features.Transports.ViewModels;
using Percolator.Desktop.ViewModels;
using R3;

/// <summary>
/// ViewModel for the DESK-01 / CONN-11 responsive 3-pane shell frame. Owns transient shell-view state
/// (drawer open/closed, active conversation header) and composes the Left Rail and Chats sidebars ([DESK-05], [DESK-06]).
/// </summary>
public sealed partial class MainWindowViewModel : ViewModelBase
{
    private readonly ICurrentPersonaService _currentPersonaService;

    /// <summary>
    /// ViewModel governing the 72px fixed Left Rail navigation sidebar ([DESK-05]).
    /// </summary>
    public TransportsSidebarViewModel TransportsSidebar { get; }

    /// <summary>
    /// ViewModel governing the 240px contextual right sidebar chats directory ([DESK-06]).
    /// </summary>
    public ChatsSidebarViewModel ChatsSidebar { get; }

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

    /// <summary>Header title for the active conversation; empty when none is selected.</summary>
    [ObservableProperty]
    private string _activeConversationTitle = string.Empty;

    /// <summary>
    /// Currently focused persona snapshot driving shell views and dialog orchestration
    /// (INV-DESKTOP-PRESENTATION-MODELS, VM-006).
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCurrentPersona))]
    private ActivePersonaModel? _currentPersona;

    /// <summary>True when a valid persona is currently active in the shell.</summary>
    public bool HasCurrentPersona => CurrentPersona != null && CurrentPersona.IdentityId.IsValid;

    public MainWindowViewModel(
        ICurrentPersonaService currentPersonaService,
        TransportsSidebarViewModel transportsSidebar,
        ChatsSidebarViewModel chatsSidebar)
    {
        _currentPersonaService = currentPersonaService ?? throw new ArgumentNullException(nameof(currentPersonaService));
        TransportsSidebar = transportsSidebar ?? throw new ArgumentNullException(nameof(transportsSidebar));
        ChatsSidebar = chatsSidebar ?? throw new ArgumentNullException(nameof(chatsSidebar));

        _currentPersona = _currentPersonaService.CurrentPersona;

        _currentPersonaService.CurrentPersonaChanged
            .ObserveOnCurrentSynchronizationContext()
            .Subscribe(p => CurrentPersona = p)
            .AddTo(ref Bag);
    }

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

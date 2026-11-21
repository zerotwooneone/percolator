// SPECIFICATION GOVERNANCE: Read Percolator.Desktop/spec/ViewModel.spec.md before modifying this file.

namespace Percolator.Desktop.Features.Shell.ViewModels;

using System;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Percolator.Desktop.Common.Navigation;
using Percolator.Desktop.Common.Services;
using Percolator.Desktop.Features.Chats.Services;
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
    private readonly IShellNavigationStateService _navigationService;
    private readonly ISessionScopeFactory _sessionScopeFactory;

    /// <summary>
    /// ViewModel governing the 72px fixed Left Rail navigation sidebar ([DESK-05]).
    /// </summary>
    public TransportsSidebarViewModel TransportsSidebar { get; }

    /// <summary>
    /// ViewModel governing the 240px contextual right sidebar chats directory ([DESK-06]).
    /// </summary>
    public ChatsSidebarViewModel ChatsSidebar { get; }

    /// <summary>
    /// Active conversation ViewModel resolved from the LRU session scope factory ([DEC-LRU-SESSION-SCOPES], [DESK-07]).
    /// Null when no conversation is currently selected.
    /// </summary>
    [ObservableProperty]
    private ChatViewModel? _currentChat;

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
        IShellNavigationStateService navigationService,
        ISessionScopeFactory sessionScopeFactory,
        TransportsSidebarViewModel transportsSidebar,
        ChatsSidebarViewModel chatsSidebar)
    {
        _currentPersonaService = currentPersonaService ?? throw new ArgumentNullException(nameof(currentPersonaService));
        _navigationService = navigationService ?? throw new ArgumentNullException(nameof(navigationService));
        _sessionScopeFactory = sessionScopeFactory ?? throw new ArgumentNullException(nameof(sessionScopeFactory));
        TransportsSidebar = transportsSidebar ?? throw new ArgumentNullException(nameof(transportsSidebar));
        ChatsSidebar = chatsSidebar ?? throw new ArgumentNullException(nameof(chatsSidebar));

        _currentPersona = _currentPersonaService.CurrentPersona;

        _currentPersonaService.CurrentPersonaChanged
            .ObserveOnCurrentSynchronizationContext()
            .Subscribe(p =>
            {
                CurrentPersona = p;
                CurrentChat = null;
                ActiveConversationTitle = string.Empty;
            })
            .AddTo(ref Bag);

        _navigationService.SelectedConversationId
            .ObserveOnCurrentSynchronizationContext()
            .Subscribe(channelId =>
            {
                if (channelId == null)
                {
                    CurrentChat = null;
                    ActiveConversationTitle = string.Empty;
                }
                else
                {
                    CurrentChat = _sessionScopeFactory.GetOrCreateChatViewModel(channelId.Value);
                    ActiveConversationTitle = CurrentChat.ConversationTitle;
                }
            })
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

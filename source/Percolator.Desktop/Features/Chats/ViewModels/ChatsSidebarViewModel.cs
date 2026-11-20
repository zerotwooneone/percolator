// SPECIFICATION GOVERNANCE: Read Percolator.Desktop/spec/ViewModel.spec.md before modifying this file.

namespace Percolator.Desktop.Features.Chats.ViewModels;

using System;
using System.Linq;
using System.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using ObservableCollections;
using Percolator.Desktop.Common.Navigation;
using Percolator.Desktop.Common.Services;
using Percolator.Desktop.Features.Chats.Models;
using Percolator.Desktop.Features.Chats.Services;
using Percolator.Desktop.Features.Transports.Services;
using Percolator.Desktop.ViewModels;
using Percolator.Domain.Channels.ValueObjects;
using R3;

/// <summary>
/// ViewModel governing the 240px contextual Chats sidebar (Right Sidebar pane in MainWindow).
/// Projects direct peer-to-peer 1:1 conversations and outbound pending items when Left Rail is Direct Messages,
/// or relayed 1:1 and group chats when Left Rail is a specific Relay ([DESK-06], [VIEW-001]).
/// </summary>
public sealed partial class ChatsSidebarViewModel : ViewModelBase
{
    private readonly IShellNavigationStateService _navigationService;
    private readonly IConversationStateService _conversationStateService;
    private readonly IRelayStateService _relayStateService;
    private readonly ILogger<ChatsSidebarViewModel> _logger;

    private readonly ObservableList<ConversationSummaryModel> _filteredConversations = new();
    private readonly object _lock = new();

    [ObservableProperty]
    private string _headerTitle = "Direct Messages";

    [ObservableProperty]
    private ConversationSummaryModel? _selectedConversation;

    [ObservableProperty]
    private bool _hasConversations;

    public ChatsSidebarViewModel(
        IShellNavigationStateService navigationService,
        IConversationStateService conversationStateService,
        IRelayStateService relayStateService,
        ILogger<ChatsSidebarViewModel> logger)
    {
        _navigationService = navigationService ?? throw new ArgumentNullException(nameof(navigationService));
        _conversationStateService = conversationStateService ?? throw new ArgumentNullException(nameof(conversationStateService));
        _relayStateService = relayStateService ?? throw new ArgumentNullException(nameof(relayStateService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        var view = _filteredConversations.CreateView(x => x);
        Conversations = SynchronizationContext.Current != null
            ? view.ToNotifyCollectionChanged(SynchronizationContextCollectionEventDispatcher.Current)
            : view.ToNotifyCollectionChanged();
        Conversations.AddTo(ref Bag);

        _navigationService.ActiveLeftRailTarget
            .ObserveOnCurrentSynchronizationContext()
            .Subscribe(target =>
            {
                UpdateHeaderTitle(target);
                RefreshFilteredConversations(target);
            })
            .AddTo(ref Bag);

        _conversationStateService.Conversations.ObserveChanged()
            .ObserveOnCurrentSynchronizationContext()
            .Subscribe(_ => RefreshFilteredConversations(_navigationService.ActiveLeftRailTarget.CurrentValue))
            .AddTo(ref Bag);

        _navigationService.SelectedConversationId
            .ObserveOnCurrentSynchronizationContext()
            .Subscribe(channelId =>
            {
                if (channelId == null)
                {
                    if (_selectedConversation != null)
                    {
                        _selectedConversation = null;
                        OnPropertyChanged(nameof(SelectedConversation));
                    }
                }
                else
                {
                    var match = _filteredConversations.FirstOrDefault(c => c.ConversationId == channelId);
                    if (!ReferenceEquals(_selectedConversation, match))
                    {
                        _selectedConversation = match;
                        OnPropertyChanged(nameof(SelectedConversation));
                    }
                }
            })
            .AddTo(ref Bag);
    }

    /// <summary>
    /// Synchronized collection of contextual conversations bound to the Right Sidebar list ([VM-019]).
    /// </summary>
    public INotifyCollectionChangedSynchronizedViewList<ConversationSummaryModel> Conversations { get; }

    partial void OnSelectedConversationChanged(ConversationSummaryModel? value)
    {
        if (value != null && value.ConversationId.HasValue)
        {
            _navigationService.SelectConversation(value.ConversationId.Value);
        }
    }

    private void UpdateHeaderTitle(LeftRailTarget target)
    {
        if (target is LeftRailTarget.DirectMessagesTarget)
        {
            HeaderTitle = "Direct Messages";
        }
        else if (target is LeftRailTarget.RelayTarget relayTarget)
        {
            var relay = _relayStateService.Relays.FirstOrDefault(r => r.RelayIdentityId == relayTarget.RelayIdentityId);
            HeaderTitle = relay != null ? relay.DisplayName : "Relayed Chats";
        }
    }

    private void RefreshFilteredConversations(LeftRailTarget target)
    {
        var all = _conversationStateService.Conversations.ToList();
        List<ConversationSummaryModel> filtered;

        if (target is LeftRailTarget.DirectMessagesTarget)
        {
            // Direct P2P conversations and outbound pending direct connections
            filtered = all.Where(c => c.RouteType == ConversationRouteType.DirectP2P || c.IsPendingOutbound).ToList();
        }
        else if (target is LeftRailTarget.RelayTarget relayTarget)
        {
            // Relayed direct messages and groups hosted on this specific relay
            filtered = all.Where(c => c.RouteType == ConversationRouteType.Relayed && c.RelayId == relayTarget.RelayIdentityId).ToList();
        }
        else
        {
            filtered = new List<ConversationSummaryModel>();
        }

        lock (_lock)
        {
            _filteredConversations.Clear();
            foreach (var item in filtered)
            {
                _filteredConversations.Add(item);
            }
        }

        HasConversations = _filteredConversations.Count > 0;
    }
}

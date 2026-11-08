using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ObservableCollections;
using Percolator.Application2.Ports;
using Percolator.Apps.Chat.Features.GroupInvitations;
using Percolator.Apps.Chat.Features.Messaging;
using Percolator.Domain.Channels.Ports;
using Percolator.Domain.Channels.ValueObjects;
using Percolator.Domain.Common;
using Percolator.Domain.Identities.Model;
using Percolator.Domain.Identities.ValueObjects;
using Percolator.Simulator.Features.DirectChat;
using Percolator.Simulator.Features.DiscoverySimulation;
using Percolator.Simulator.Features.GroupMessaging;
using Percolator.Simulator.Features.Logging;
using Percolator.Simulator.Features.RichChatAndProfiles;
using Percolator.Simulator.Features.RelayedChat;
using Percolator.Simulator.Features.Scenarios;
using Percolator.Simulator.Features.Scenarios.Model;
using Percolator.Simulator.Features.Shell;
using R3;

namespace Percolator.Simulator.Features.Topology;

internal sealed record ConversationItemViewModel(
    string DisplayTitle,
    bool IsGroup,
    SimulatedNodeId? PeerNodeId,
    ChannelId? GroupChannelId)
{
    public override string ToString() => DisplayTitle;
}

internal sealed partial class PersonaCardViewModel : ViewModelBase
{
    private readonly IStatusToastService _toastService;
    private readonly ISimulatorStateBus _stateBus;
    private readonly DirectHandshakeOrchestrator _handshakeOrchestrator;
    private readonly DirectChatCoordinator _chatCoordinator;
    private readonly AutopilotPongResponder _pongResponder;
    private readonly ISelectionStateService _selectionState = null!;
    private readonly ISimulatedWorldStateService _worldState = null!;
    private readonly ISimulatedPersonaHost? _host;
    private readonly ISimulatorLogSink _logSink;
    private readonly IFlightRecorderStateService? _flightRecorder;
    private readonly RelayProvisioningCoordinator _relayCoordinator;
    private readonly RichChatActionCoordinator _richChatCoordinator;
    private readonly ProfileSyncCoordinator _profileCoordinator;
    private readonly GroupChatCoordinator _groupCoordinator;
    private readonly DiscoverySimulationCoordinator _discoveryCoordinator;

    private readonly CoalescedActionGate _chatTranscriptGate;
    private readonly CoalescedActionGate _pendingHandshakeGate;

    public SimulatedNodeId NodeId { get; }

    [ObservableProperty]
    private string _displayName;

    [ObservableProperty]
    private string _boundEndpoint;

    [ObservableProperty]
    private Bitmap? _identiconBitmap;

    [ObservableProperty]
    private bool _isListening = true;

    // Feature Flags (CTL-PER-03..06)
    [ObservableProperty]
    private bool _allowPeerDiscovery = true;

    [ObservableProperty]
    private bool _isChatTabActive = true;

    [ObservableProperty]
    private bool _isDiscoveryTabActive = false;

    [ObservableProperty]
    private bool _allowFileTransfers = true;

    [ObservableProperty]
    private bool _sendReadReceipts = true;

    [ObservableProperty]
    private bool _relayOnlyRouting = false;

    // Autopilot Modes (CTL-PER-07..12)
    [ObservableProperty]
    private bool _autoAcceptHandshakes = false;

    [ObservableProperty]
    private bool _autoReplyPong = false;

    [ObservableProperty]
    private bool _autoReplenishPreKeys = true;

    [ObservableProperty]
    private bool _autoShareProfile = true;

    [ObservableProperty]
    private bool _autoAcceptGroupInvites = false;

    [ObservableProperty]
    private bool _autoAcceptFileTransfers = false;

    // Pending Handshake Banner State (CTL-PER-14)
    [ObservableProperty]
    private bool _hasPendingHandshake;

    [ObservableProperty]
    private string _pendingHandshakeText = string.Empty;

    // Pending Group Invite Banner State (CTL-PER-15)
    [ObservableProperty]
    private bool _hasPendingGroupInvite;

    [ObservableProperty]
    private string _pendingGroupInviteText = string.Empty;

    private PublicIdentityId? _pendingSenderId;
    private InvitationId? _pendingInvitationId;
    private Guid _lastMarkedReadMessageId;

    // Conversation Selector (CTL-PER-17)
    public ObservableCollection<ConversationItemViewModel> AvailableConversations { get; } = new();

    [ObservableProperty]
    private ConversationItemViewModel? _selectedConversation;

    // Compose State (CTL-PER-18)
    [ObservableProperty]
    private string _composeMessageText = string.Empty;

    [ObservableProperty]
    private int _selectedTimerIndex;

    public ObservableCollection<ChatMessageItemViewModel> ChatMessages { get; } = new();

    public DiscoveryTabViewModel? DiscoveryTab { get; }

    public PersonaCardViewModel(
        SimulatedNodeSummary summary,
        PersonaCardDependencies deps,
        ISimulatedPersonaHost? host = null)
    {
        ArgumentNullException.ThrowIfNull(summary);
        ArgumentNullException.ThrowIfNull(deps);

        NodeId = summary.Id;
        _displayName = summary.DisplayName;
        _boundEndpoint = summary.Subtitle;
        _toastService = deps.ToastService;
        _stateBus = deps.StateBus;
        _handshakeOrchestrator = deps.HandshakeOrchestrator;
        _chatCoordinator = deps.ChatCoordinator;
        _pongResponder = deps.PongResponder;
        _selectionState = deps.SelectionState ?? throw new ArgumentNullException(nameof(deps.SelectionState));
        _worldState = deps.WorldState ?? throw new ArgumentNullException(nameof(deps.WorldState));
        _host = host;
        _logSink = deps.LogSink;
        _flightRecorder = deps.FlightRecorder;
        _relayCoordinator = deps.RelayCoordinator;
        _richChatCoordinator = deps.RichChatCoordinator;
        _profileCoordinator = deps.ProfileCoordinator;
        _groupCoordinator = deps.GroupCoordinator ?? throw new ArgumentNullException(nameof(deps.GroupCoordinator));
        _discoveryCoordinator = deps.DiscoveryCoordinator ?? throw new ArgumentNullException(nameof(deps.DiscoveryCoordinator));

        // 30 Hz refresh ceiling for chat transcript with 0ms leading edge
        _chatTranscriptGate = new CoalescedActionGate(
            TimeSpan.FromMilliseconds(33),
            ExecuteRefreshChatTranscript);
        _chatTranscriptGate.AddTo(ref Disposables);

        // 20 Hz refresh ceiling for pending handshakes and group invites
        _pendingHandshakeGate = new CoalescedActionGate(
            TimeSpan.FromMilliseconds(50),
            ExecuteRefreshPendingHandshakes);
        _pendingHandshakeGate.AddTo(ref Disposables);

        if (host?.IdenticonPng is not null && host.IdenticonPng.Length > 0)
        {
            using var ms = new MemoryStream(host.IdenticonPng);
            _identiconBitmap = new Bitmap(ms);
        }

        if (host is not null)
        {
            _isListening = host.IsListening.CurrentValue;
            _autoShareProfile = host.AutoShareProfile;
            _autoAcceptGroupInvites = host.AutoAcceptGroupInvites;
            _allowPeerDiscovery = host.AllowPeerDiscovery;
            DiscoveryTab = new DiscoveryTabViewModel(
                host,
                _discoveryCoordinator,
                _worldState,
                _toastService,
                deps.VirtualClock);
        }

        // Subscribe to selection changes to switch active peer conversation dynamically
        if (_selectionState != null)
        {
            _selectionState.PrimarySelectedNode
                .Subscribe(_ =>
                {
                    RefreshConversations();
                    RefreshChatTranscript();
                })
                .AddTo(ref Disposables);

            _selectionState.SecondarySelectedNode
                .Subscribe(_ =>
                {
                    RefreshConversations();
                    RefreshChatTranscript();
                })
                .AddTo(ref Disposables);
        }

        // Watch for newly registered personas in world state
        if (_worldState != null)
        {
            _worldState.Personas.ObserveChanged()
                .Subscribe(_ =>
                {
                    RefreshConversations();
                    RefreshChatTranscript();
                })
                .AddTo(ref Disposables);

            _worldState.Groups.ObserveChanged()
                .Subscribe(_ =>
                {
                    RefreshConversations();
                    RefreshChatTranscript();
                })
                .AddTo(ref Disposables);
        }

        // Subscribe to cross-host reactive signals
        if (_host != null)
        {
            _stateBus.Signals
                .Where(s => s.NodeId == NodeId && s.Category == SignalCategory.PendingHandshakeChanged)
                .Subscribe(_ => RefreshPendingHandshakes())
                .AddTo(ref Disposables);

            _stateBus.Signals
                .Where(s => (s.NodeId == NodeId ||
                             s.Details == _host.PublicIdentityId.ToString() ||
                             (s.Payload is ChatTranscriptSignalPayload cp && cp.RemotePeerId == _host.PublicIdentityId)) &&
                            s.Category == SignalCategory.ChatTranscriptChanged)
                .Subscribe(_ =>
                {
                    RefreshConversations();
                    RefreshChatTranscript();
                    RefreshPendingHandshakes();
                })
                .AddTo(ref Disposables);

            _stateBus.Signals
                .Where(s => s.Category == SignalCategory.GroupStateChanged)
                .Subscribe(_ =>
                {
                    RefreshConversations();
                    RefreshChatTranscript();
                    RefreshPendingHandshakes();
                })
                .AddTo(ref Disposables);

            _stateBus.Signals
                .Where(s => s.NodeId == NodeId && s.Category == SignalCategory.RatchetSessionChanged)
                .Subscribe(_ =>
                {
                    RefreshPendingHandshakes();
                    RefreshChatTranscript();
                })
                .AddTo(ref Disposables);

            _stateBus.Signals
                .Where(s => s.Category == SignalCategory.ProfileChanged)
                .Subscribe(signal =>
                {
                    if (signal.NodeId == NodeId && _host != null)
                    {
                        UiTaskRunner.PostToUi(() =>
                        {
                            DisplayName = _host.DisplayName;
                            if (_host.IdenticonPng is { Length: > 0 })
                            {
                                using var ms = new MemoryStream(_host.IdenticonPng);
                                IdenticonBitmap = new Bitmap(ms);
                            }
                        });
                    }
                    RefreshChatTranscript();
                })
                .AddTo(ref Disposables);

            _stateBus.Signals
                .Where(s => s.Category == SignalCategory.HostedPreKeyCountChanged)
                .Subscribe(signal =>
                {
                    if (AutoReplenishPreKeys && _host?.HomeRelay != null)
                    {
                        PublicIdentityId? ownerId = (signal.Payload as HostedPreKeyCountPayload)?.OwnerId;
                        int? available = (signal.Payload as HostedPreKeyCountPayload)?.AvailableCount;

                        if (!ownerId.HasValue && signal.Details != null && signal.Details.StartsWith(_host.PublicIdentityId.ToString()))
                        {
                            var parts = signal.Details.Split(':');
                            if (parts.Length == 2 && int.TryParse(parts[1], out var parsedCount))
                            {
                                ownerId = _host.PublicIdentityId;
                                available = parsedCount;
                            }
                        }

                        if (ownerId == _host.PublicIdentityId && available.HasValue && available.Value < 5)
                        {
                            int count = available.Value;
                            UiTaskRunner.FireAndForget(async () =>
                            {
                                _logSink.Log(new SimulatorLogEntry(
                                    DateTimeOffset.UtcNow,
                                    LogLevel.Information,
                                    DisplayName,
                                    NodeId,
                                    "Autopilot",
                                    $"Auto-replenishing prekeys: hosted OPK count dropped to {count} (< 5). Uploading 25 fresh OPKs."));

                                await _relayCoordinator.PublishPreKeysAsync(_host, _host.HomeRelay, 25);
                            },
                            category: "Autopilot",
                            logSink: _logSink,
                            nodeName: DisplayName,
                            nodeId: NodeId);
                        }
                    }
                })
                .AddTo(ref Disposables);

            // Initial refresh
            RefreshConversations();
            RefreshPendingHandshakes();
            RefreshChatTranscript();
        }
    }

    partial void OnAllowPeerDiscoveryChanged(bool value)
    {
        if (_host != null)
        {
            _host.AllowPeerDiscovery = value;
            UiTaskRunner.FireAndForget(
                () => _discoveryCoordinator.SetDiscoveryVisibilityAsync(_host, value),
                category: "Discovery",
                logSink: _logSink,
                nodeName: DisplayName,
                nodeId: NodeId);
        }
    }

    [RelayCommand]
    private void SelectChatTab()
    {
        IsChatTabActive = true;
        IsDiscoveryTabActive = false;
    }

    [RelayCommand]
    private void SelectDiscoveryTab()
    {
        IsChatTabActive = false;
        IsDiscoveryTabActive = true;
    }

    private void RefreshPendingHandshakes() => _pendingHandshakeGate.RequestExecution();

    private void ExecuteRefreshPendingHandshakes()
    {
        if (_host == null) return;

        UiTaskRunner.FireAndForget(
            async () =>
            {
                using var scope = _host.Services.CreateScope();

                // 1. Direct handshakes
                var contactQuery = scope.ServiceProvider.GetRequiredService<IPeerContactQueryService>();
                var contacts = await contactQuery.GetContactSummariesAsync(_host.PublicIdentityId);
                var pending = contacts.FirstOrDefault(c => c.State == ContactState.PendingApproval);

                // 2. Group invitations (CQRS Read Model)
                var groupInviteQuery = scope.ServiceProvider.GetRequiredService<IGroupInvitationQueryService>();
                var pendingInvites = await groupInviteQuery.GetPendingInvitationsAsync();
                var pendingInvite = pendingInvites.FirstOrDefault(i => i.Status == GroupInvitationStatus.Pending);

                UiTaskRunner.PostToUi(() =>
                {
                    if (pending != null)
                    {
                        _pendingSenderId = pending.IdentityId;
                        var senderHost = _worldState.FindPersonaHostByIdentity(pending.IdentityId);
                        var senderName = senderHost?.DisplayName ?? pending.IdentityId.ToString()[..8];
                        PendingHandshakeText = $"Pending handshake from {senderName}";
                        HasPendingHandshake = true;

                        if (AutoAcceptHandshakes)
                        {
                            _ = AcceptPendingHandshakeAsync();
                        }
                    }
                    else
                    {
                        _pendingSenderId = null;
                        HasPendingHandshake = false;
                        PendingHandshakeText = string.Empty;
                    }

                    if (pendingInvite != null)
                    {
                        _pendingInvitationId = pendingInvite.InvitationId;
                        var inviterHost = _worldState.FindPersonaHostByIdentity(pendingInvite.InviterId);
                        var inviterName = inviterHost?.DisplayName ?? pendingInvite.InviterId.ToString()[..8];
                        PendingGroupInviteText = $"Group invitation to '{pendingInvite.GroupName}' from {inviterName}";
                        HasPendingGroupInvite = true;

                        if (AutoAcceptGroupInvites)
                        {
                            _ = AcceptPendingGroupInviteAsync();
                        }
                    }
                    else
                    {
                        _pendingInvitationId = null;
                        HasPendingGroupInvite = false;
                        PendingGroupInviteText = string.Empty;
                    }
                });
            },
            category: "PersonaCard",
            logSink: _logSink,
            nodeName: DisplayName,
            nodeId: NodeId);
    }

    private void RefreshConversations()
    {
        if (_worldState == null) return;

        UiTaskRunner.FireAndForget(
            async () =>
            {
                var list = new List<ConversationItemViewModel>();

                // 1. Direct Peers
                foreach (var persona in _worldState.Personas)
                {
                    if (persona.Id != NodeId && IsActivePersona(persona.Id))
                    {
                        list.Add(new ConversationItemViewModel($"👤 {persona.DisplayName}", IsGroup: false, persona.Id, null));
                    }
                }

                // 2. Groups (Scoped strictly to this persona's joined groups)
                if (_host != null)
                {
                    try
                    {
                        using var scope = _host.Services.CreateScope();
                        var chatQuery = scope.ServiceProvider.GetRequiredService<ISimulatorChatQueryService>();
                        var groups = await chatQuery.GetJoinedGroupsAsync(_host.PublicIdentityId);
                        foreach (var group in groups)
                        {
                            list.Add(new ConversationItemViewModel($"👥 {group.Name}", IsGroup: true, null, group.ChannelId));
                        }
                    }
                    catch (Exception ex)
                    {
                        _logSink.Log(new SimulatorLogEntry(
                            DateTimeOffset.UtcNow,
                            LogLevel.Debug,
                            DisplayName,
                            NodeId,
                            "PersonaCard",
                            $"Error loading joined groups for persona conversations: {ex.Message}"));
                    }
                }

                UiTaskRunner.PostToUi(() =>
                {
                    var currentSelected = SelectedConversation;

                    // Sync list
                    if (!AvailableConversations.SequenceEqual(list))
                    {
                        AvailableConversations.Clear();
                        foreach (var item in list)
                        {
                            AvailableConversations.Add(item);
                        }
                    }

                    // Restore selection if still present in available conversations
                    if (currentSelected != null)
                    {
                        var match = AvailableConversations.FirstOrDefault(c =>
                            (c.IsGroup && currentSelected.IsGroup && c.GroupChannelId == currentSelected.GroupChannelId) ||
                            (!c.IsGroup && !currentSelected.IsGroup && c.PeerNodeId == currentSelected.PeerNodeId));
                        if (match != null)
                        {
                            SelectedConversation = match;
                            return;
                        }
                    }

                    // Otherwise fall back to the first available conversation (or null if none)
                    SelectedConversation = AvailableConversations.FirstOrDefault();
                });
            },
            category: "PersonaCard",
            logSink: _logSink,
            nodeName: DisplayName,
            nodeId: NodeId);
    }

    partial void OnSelectedConversationChanged(ConversationItemViewModel? value)
    {
        RefreshChatTranscript();
    }

    private void RefreshChatTranscript() => _chatTranscriptGate.RequestExecution();

    partial void OnSendReadReceiptsChanged(bool value)
    {
        if (value)
        {
            RefreshChatTranscript();
        }
        else
        {
            _lastMarkedReadMessageId = Guid.Empty;
        }
    }

    private void ExecuteRefreshChatTranscript()
    {
        if (_host == null) return;

        UiTaskRunner.FireAndForget(
            async () =>
            {
                IReadOnlyList<ChatMessageItemViewModel> transcript;

                if (SelectedConversation?.IsGroup == true && SelectedConversation.GroupChannelId.HasValue)
                {
                    var channelId = SelectedConversation.GroupChannelId.Value;
                    transcript = await _groupCoordinator.GetGroupTranscriptAsync(_host, channelId);
                }
                else
                {
                    var activePeer = GetActivePeerSummary();
                    if (activePeer == null)
                    {
                        UiTaskRunner.PostToUi(() => ChatMessages.Clear());
                        return;
                    }

                    transcript = await _chatCoordinator.GetTranscriptAsync(NodeId, activePeer.Id);

                    if (SendReadReceipts && IsListening)
                    {
                        var lastUnreadIncoming = transcript.LastOrDefault(m => !m.IsOutgoing && !m.IsRead);
                        if (lastUnreadIncoming != null &&
                            Guid.TryParse(lastUnreadIncoming.MessageId, out var msgGuid) &&
                            msgGuid != _lastMarkedReadMessageId)
                        {
                            var success = await _chatCoordinator.MarkConversationAsReadAsync(NodeId, activePeer.Id, msgGuid);
                            if (success)
                            {
                                _lastMarkedReadMessageId = msgGuid;
                            }
                        }
                    }
                }

                UiTaskRunner.PostToUi(() =>
                {
                    if (ChatMessages.Count == transcript.Count &&
                        ChatMessages.SequenceEqual(transcript))
                    {
                        return; // No-op: transcript already up to date
                    }

                    if (ChatMessages.Count == transcript.Count)
                    {
                        for (int i = 0; i < transcript.Count; i++)
                        {
                            if (ChatMessages[i] != transcript[i])
                            {
                                ChatMessages[i] = transcript[i];
                            }
                        }
                        return;
                    }

                    if (ChatMessages.Count > 0 &&
                        transcript.Count > ChatMessages.Count &&
                        ChatMessages.Zip(transcript).All(pair => pair.First == pair.Second))
                    {
                        // Append new items smoothly
                        var newItems = transcript.Skip(ChatMessages.Count).ToList();
                        foreach (var msg in newItems)
                        {
                            ChatMessages.Add(msg);
                        }
                    }
                    else
                    {
                        ChatMessages.Clear();
                        foreach (var msg in transcript)
                        {
                            ChatMessages.Add(msg);
                        }
                    }
                });
            },
            category: "PersonaCard",
            logSink: _logSink,
            nodeName: DisplayName,
            nodeId: NodeId);
    }

    private SimulatedNodeSummary? GetActivePeerSummary()
    {
        if (_worldState == null) return null;

        // 1. If a direct peer conversation is selected in dropdown, use that
        if (SelectedConversation is { IsGroup: false, PeerNodeId: not null })
        {
            var peer = _worldState.Personas.FirstOrDefault(p => p.Id == SelectedConversation.PeerNodeId && IsActivePersona(p.Id));
            if (peer != null) return peer;
        }

        // 2. Check if selection state points to a VALID, currently active persona in world state
        if (_selectionState != null)
        {
            var primary = _selectionState.PrimarySelectedNode.CurrentValue;
            if (primary != null && primary.NodeType == SimulatedNodeType.Persona && primary.Id != NodeId && IsActivePersona(primary.Id))
            {
                return primary;
            }

            var secondary = _selectionState.SecondarySelectedNode.CurrentValue;
            if (secondary != null && secondary.NodeType == SimulatedNodeType.Persona && secondary.Id != NodeId && IsActivePersona(secondary.Id))
            {
                return secondary;
            }
        }

        // 3. Fall back to any active persona in the world that is not this node
        return _worldState.Personas.FirstOrDefault(p => p.Id != NodeId && IsActivePersona(p.Id));
    }

    private bool IsActivePersona(SimulatedNodeId nodeId)
    {
        return _worldState?.GetPersonaHost(nodeId) != null;
    }

    partial void OnIsListeningChanged(bool value)
    {
        if (_host != null)
        {
            UiTaskRunner.FireAndForget(async () =>
            {
                await _host.SetListeningAsync(value).ConfigureAwait(false);
                if (value)
                {
                    UiTaskRunner.PostToUi(() => RefreshChatTranscript());
                }
            }, "HostLifecycle");
        }
        _flightRecorder?.RecordStep(new SetListeningStateStep(DisplayName, value));
    }

    partial void OnAutoReplyPongChanged(bool value)
    {
        _pongResponder.SetAutoPongEnabled(NodeId, value);
    }

    partial void OnAutoShareProfileChanged(bool value)
    {
        if (_host != null)
        {
            _host.AutoShareProfile = value;
        }
    }

    partial void OnAutoAcceptGroupInvitesChanged(bool value)
    {
        if (_host != null)
        {
            _host.AutoAcceptGroupInvites = value;
        }
    }

    [RelayCommand]
    private async Task RerollProfileAsync()
    {
        if (_host == null) return;

        var success = await UiTaskRunner.RunAsync(
            () => _profileCoordinator.RerollProfileAsync(NodeId),
            category: "ProfileSync",
            logSink: _logSink,
            toastService: _toastService,
            nodeName: DisplayName,
            nodeId: NodeId);

        if (success)
        {
            DisplayName = _host.DisplayName;
            if (_host.IdenticonPng is { Length: > 0 })
            {
                using var ms = new MemoryStream(_host.IdenticonPng);
                IdenticonBitmap = new Bitmap(ms);
            }
        }
    }

    [RelayCommand]
    private async Task RegisterRelayAsync()
    {
        if (_host == null) return;
        var relay = _host.HomeRelay ?? _worldState.GetAllRelayHosts().FirstOrDefault();
        if (relay == null)
        {
            _toastService.ShowToast("Relay Missing", "Spawn a Relay node first in the top toolbar.", ToastSeverity.Warning);
            return;
        }

        var success = await UiTaskRunner.RunAsync(
            () => _relayCoordinator.RegisterMailboxAsync(_host, relay),
            category: "RelayProvisioning",
            toastService: _toastService);

        if (success)
        {
            _flightRecorder?.RecordStep(new RegisterMailboxesStep(relay.DisplayName, new[] { DisplayName }));
            _toastService.ShowToast("Relay Registered", $"Mailbox registered on '{relay.DisplayName}'.", ToastSeverity.Success);
        }
    }

    [RelayCommand]
    private async Task PublishPreKeysAsync()
    {
        if (_host == null) return;
        var relay = _host.HomeRelay ?? _worldState.GetAllRelayHosts().FirstOrDefault();
        if (relay == null)
        {
            _toastService.ShowToast("Relay Missing", "Spawn a Relay node first in the top toolbar.", ToastSeverity.Warning);
            return;
        }

        var success = await UiTaskRunner.RunAsync(
            () => _relayCoordinator.PublishPreKeysAsync(_host, relay, 25),
            category: "RelayProvisioning",
            toastService: _toastService);

        if (success)
        {
            _flightRecorder?.RecordStep(new PublishPreKeysStep(relay.DisplayName, new[] { DisplayName }, 25));
            _toastService.ShowToast("PreKeys Published", $"Published 25 OPKs to '{relay.DisplayName}'.", ToastSeverity.Success);
        }
    }

    [RelayCommand]
    private async Task AcceptPendingHandshakeAsync()
    {
        if (!_pendingSenderId.HasValue) return;
        var senderId = _pendingSenderId.Value;

        var success = await UiTaskRunner.RunAsync(
            () => _handshakeOrchestrator.AcceptHandshakeAsync(NodeId, senderId),
            category: "Handshake",
            logSink: _logSink,
            toastService: _toastService,
            nodeName: DisplayName,
            nodeId: NodeId);

        if (success)
        {
            HasPendingHandshake = false;
            PendingHandshakeText = string.Empty;
            _pendingSenderId = null;
        }
    }

    [RelayCommand]
    private async Task RejectPendingHandshakeAsync()
    {
        if (!_pendingSenderId.HasValue) return;
        var senderId = _pendingSenderId.Value;

        var success = await UiTaskRunner.RunAsync(
            () => _handshakeOrchestrator.RejectHandshakeAsync(NodeId, senderId, block: false),
            category: "Handshake",
            logSink: _logSink,
            toastService: _toastService,
            nodeName: DisplayName,
            nodeId: NodeId);

        if (success)
        {
            HasPendingHandshake = false;
            PendingHandshakeText = string.Empty;
            _pendingSenderId = null;
        }
    }

    [RelayCommand]
    private async Task BlockPendingHandshakeAsync()
    {
        if (!_pendingSenderId.HasValue) return;
        var senderId = _pendingSenderId.Value;

        await UiTaskRunner.RunAsync(
            () => _handshakeOrchestrator.RejectHandshakeAsync(NodeId, senderId, block: true),
            category: "Handshake",
            logSink: _logSink,
            toastService: _toastService,
            nodeName: DisplayName,
            nodeId: NodeId);
    }

    [RelayCommand]
    private async Task AcceptPendingGroupInviteAsync()
    {
        if (_host == null || !_pendingInvitationId.HasValue) return;
        var inviteId = _pendingInvitationId.Value;

        var success = await UiTaskRunner.RunAsync(
            () => _groupCoordinator.AcceptGroupInviteAsync(_host, inviteId),
            category: "GroupInvitation",
            logSink: _logSink,
            toastService: _toastService,
            nodeName: DisplayName,
            nodeId: NodeId);

        if (success)
        {
            HasPendingGroupInvite = false;
            PendingGroupInviteText = string.Empty;
            _pendingInvitationId = null;
            RefreshPendingHandshakes();
            RefreshConversations();
        }
    }

    [RelayCommand]
    private async Task DeclinePendingGroupInviteAsync()
    {
        if (_host == null || !_pendingInvitationId.HasValue) return;
        var inviteId = _pendingInvitationId.Value;

        var success = await UiTaskRunner.RunAsync(
            () => _groupCoordinator.DeclineGroupInviteAsync(_host, inviteId),
            category: "GroupInvitation",
            logSink: _logSink,
            toastService: _toastService,
            nodeName: DisplayName,
            nodeId: NodeId);

        if (success)
        {
            HasPendingGroupInvite = false;
            PendingGroupInviteText = string.Empty;
            _pendingInvitationId = null;
            RefreshPendingHandshakes();
        }
    }

    [RelayCommand]
    private async Task SendMessageAsync()
    {
        if (string.IsNullOrWhiteSpace(ComposeMessageText)) return;
        var text = ComposeMessageText;
        ComposeMessageText = string.Empty;

        // Group message branch
        if (SelectedConversation?.IsGroup == true && SelectedConversation.GroupChannelId.HasValue)
        {
            if (_host == null) return;
            var channelId = SelectedConversation.GroupChannelId.Value;
            var groupTitle = SelectedConversation.DisplayTitle.Replace("👥 ", "");

            var success = await UiTaskRunner.RunAsync(
                () => _groupCoordinator.SendGroupMessageAsync(_host, channelId, text),
                category: "GroupChat",
                logSink: _logSink,
                toastService: _toastService,
                nodeName: DisplayName,
                nodeId: NodeId);

            if (success)
            {
                _flightRecorder?.RecordStep(new SendGroupMessageStep(DisplayName, groupTitle, text));
            }
            return;
        }

        // Direct 1:1 message branch
        var expireSeconds = SelectedTimerIndex switch
        {
            1 => 60,
            2 => 3600,
            _ => (int?)null
        };

        var peer = GetActivePeerSummary();
        if (peer == null)
        {
            _toastService.ShowToast("Send Failed", "No remote peer found. Add another persona first.", ToastSeverity.Warning);
            return;
        }

        var peerId = peer.Id;
        var successDirect = await UiTaskRunner.RunAsync(
            () => _chatCoordinator.SendDirectMessageAsync(NodeId, peerId, text, expireSeconds),
            category: "DirectChat",
            logSink: _logSink,
            toastService: _toastService,
            nodeName: DisplayName,
            nodeId: NodeId);

        if (successDirect)
        {
            if (expireSeconds.HasValue && expireSeconds.Value > 0)
            {
                _flightRecorder?.RecordStep(new SendDisappearingMessageStep(DisplayName, peer.DisplayName, text, expireSeconds.Value));
            }
            else
            {
                _flightRecorder?.RecordStep(new SendDirectMessageStep(DisplayName, peer.DisplayName, text));
            }
        }
    }

    [RelayCommand]
    private async Task ReactAsync(ChatMessageItemViewModel? item)
    {
        if (item == null || !Guid.TryParse(item.MessageId, out var msgGuid)) return;
        var peer = GetActivePeerSummary();
        if (peer == null) return;

        var success = await UiTaskRunner.RunAsync(
            () => _richChatCoordinator.AddReactionAsync(NodeId, peer.Id, msgGuid, "👍"),
            category: "RichChat",
            logSink: _logSink,
            toastService: _toastService,
            nodeName: DisplayName,
            nodeId: NodeId);

        if (success)
        {
            _flightRecorder?.RecordStep(new AddReactionStep(DisplayName, peer.DisplayName, item.Content, "👍"));
        }
    }

    [RelayCommand]
    private async Task EditMessageAsync(ChatMessageItemViewModel? item)
    {
        if (item == null || !item.IsOutgoing || !Guid.TryParse(item.MessageId, out var msgGuid)) return;
        var peer = GetActivePeerSummary();
        if (peer == null) return;

        var newContent = !string.IsNullOrWhiteSpace(ComposeMessageText)
            ? ComposeMessageText.Trim()
            : $"{item.Content} (edited)";

        if (!string.IsNullOrWhiteSpace(ComposeMessageText))
        {
            ComposeMessageText = string.Empty;
        }

        var oldContent = item.Content;
        var success = await UiTaskRunner.RunAsync(
            () => _richChatCoordinator.EditMessageAsync(NodeId, peer.Id, msgGuid, newContent),
            category: "RichChat",
            logSink: _logSink,
            toastService: _toastService,
            nodeName: DisplayName,
            nodeId: NodeId);

        if (success)
        {
            _flightRecorder?.RecordStep(new EditMessageStep(DisplayName, peer.DisplayName, oldContent, newContent));
        }
    }

    [RelayCommand]
    private async Task DeleteMessageAsync(ChatMessageItemViewModel? item)
    {
        if (item == null || !item.IsOutgoing || !Guid.TryParse(item.MessageId, out var msgGuid)) return;
        var peer = GetActivePeerSummary();
        if (peer == null) return;

        var messageContent = item.Content;
        var success = await UiTaskRunner.RunAsync(
            () => _richChatCoordinator.DeleteMessageAsync(NodeId, peer.Id, msgGuid),
            category: "RichChat",
            logSink: _logSink,
            toastService: _toastService,
            nodeName: DisplayName,
            nodeId: NodeId);

        if (success)
        {
            _flightRecorder?.RecordStep(new DeleteMessageStep(DisplayName, peer.DisplayName, messageContent));
        }
    }

    public override void Dispose()
    {
        DiscoveryTab?.Dispose();
        base.Dispose();
    }
}

using System;
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
using Percolator.Domain.Channels.Ports;
using Percolator.Domain.Common;
using Percolator.Domain.Identities.Model;
using Percolator.Domain.Identities.ValueObjects;
using Percolator.Simulator.Features.DirectChat;
using Percolator.Simulator.Features.Logging;
using Percolator.Simulator.Features.RelayedChat;
using Percolator.Simulator.Features.Scenarios;
using Percolator.Simulator.Features.Scenarios.Model;
using Percolator.Simulator.Features.Shell;
using R3;

namespace Percolator.Simulator.Features.Topology;

internal sealed partial class PersonaCardViewModel : ViewModelBase
{
    private readonly IStatusToastService _toastService;
    private readonly ISimulatorStateBus? _stateBus;
    private readonly DirectHandshakeOrchestrator? _handshakeOrchestrator;
    private readonly DirectChatCoordinator? _chatCoordinator;
    private readonly AutopilotPongResponder? _pongResponder;
    private readonly ISelectionStateService? _selectionState;
    private readonly ISimulatedWorldStateService? _worldState;
    private readonly ISimulatedPersonaHost? _host;
    private readonly ISimulatorLogSink? _logSink;
    private readonly IFlightRecorderStateService? _flightRecorder;
    private readonly RelayProvisioningCoordinator? _relayCoordinator;

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

    private PublicIdentityId? _pendingSenderId;
    private Guid _lastMarkedReadMessageId;

    // Compose State (CTL-PER-18)
    [ObservableProperty]
    private string _composeMessageText = string.Empty;

    public ObservableCollection<ChatMessageItemViewModel> ChatMessages { get; } = new();

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
        _selectionState = deps.SelectionState;
        _worldState = deps.WorldState;
        _host = host;
        _logSink = deps.LogSink;
        _flightRecorder = deps.FlightRecorder;
        _relayCoordinator = deps.RelayCoordinator;

        // 30 Hz refresh ceiling for chat transcript with 0ms leading edge
        _chatTranscriptGate = new CoalescedActionGate(
            TimeSpan.FromMilliseconds(33),
            ExecuteRefreshChatTranscript);
        _chatTranscriptGate.AddTo(ref Disposables);

        // 20 Hz refresh ceiling for pending handshakes
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
        }

        // Subscribe to selection changes to switch active peer conversation dynamically
        if (_selectionState != null)
        {
            _selectionState.PrimarySelectedNode
                .Subscribe(_ => RefreshChatTranscript())
                .AddTo(ref Disposables);

            _selectionState.SecondarySelectedNode
                .Subscribe(_ => RefreshChatTranscript())
                .AddTo(ref Disposables);
        }

        // Watch for newly registered personas in world state
        if (_worldState != null)
        {
            _worldState.Personas.ObserveChanged()
                .Subscribe(_ => RefreshChatTranscript())
                .AddTo(ref Disposables);
        }

        // Subscribe to cross-host reactive signals
        if (_stateBus != null && _host != null)
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
                .Subscribe(_ => RefreshChatTranscript())
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
                .Where(s => s.Category == SignalCategory.HostedPreKeyCountChanged)
                .Subscribe(signal =>
                {
                    if (AutoReplenishPreKeys && _host?.HomeRelay != null && _relayCoordinator != null)
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
                                _logSink?.Log(new SimulatorLogEntry(
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
            RefreshPendingHandshakes();
            RefreshChatTranscript();
        }
    }

    private void RefreshPendingHandshakes() => _pendingHandshakeGate.RequestExecution();

    private void ExecuteRefreshPendingHandshakes()
    {
        if (_host == null) return;

        UiTaskRunner.FireAndForget(
            async () =>
            {
                using var scope = _host.Services.CreateScope();
                var contactQuery = scope.ServiceProvider.GetRequiredService<IPeerContactQueryService>();
                var contacts = await contactQuery.GetContactSummariesAsync(_host.PublicIdentityId);
                var pending = contacts.FirstOrDefault(c => c.State == ContactState.PendingApproval);

                UiTaskRunner.PostToUi(() =>
                {
                    if (pending != null)
                    {
                        _pendingSenderId = pending.IdentityId;
                        var senderHost = _worldState?.FindPersonaHostByIdentity(pending.IdentityId);
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
                });
            },
            category: "PersonaCard",
            logSink: _logSink,
            nodeName: DisplayName,
            nodeId: NodeId);
    }

    private void RefreshChatTranscript() => _chatTranscriptGate.RequestExecution();

    partial void OnSendReadReceiptsChanged(bool value)
    {
        if (value)
        {
            RefreshChatTranscript();
        }
    }

    private void ExecuteRefreshChatTranscript()
    {
        if (_host == null || _chatCoordinator == null) return;

        UiTaskRunner.FireAndForget(
            async () =>
            {
                var activePeer = GetActivePeerSummary();
                if (activePeer == null)
                {
                    UiTaskRunner.PostToUi(() => ChatMessages.Clear());
                    return;
                }

                var transcript = await _chatCoordinator.GetTranscriptAsync(NodeId, activePeer.Id);

                if (SendReadReceipts && IsListening)
                {
                    var lastUnreadIncoming = transcript.LastOrDefault(m => !m.IsOutgoing && !m.IsRead);
                    if (lastUnreadIncoming != null &&
                        Guid.TryParse(lastUnreadIncoming.MessageId, out var msgGuid) &&
                        msgGuid != _lastMarkedReadMessageId)
                    {
                        _lastMarkedReadMessageId = msgGuid;
                        _ = _chatCoordinator.MarkConversationAsReadAsync(NodeId, activePeer.Id, msgGuid);
                    }
                }

                UiTaskRunner.PostToUi(() =>
                {
                    if (ChatMessages.Count == transcript.Count &&
                        ChatMessages.SequenceEqual(transcript))
                    {
                        return; // No-op: transcript already up to date
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

        // 1. Check if selection state points to a VALID, currently active persona in world state
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

        // 2. Fall back to any active persona in the world that is not this node
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
            UiTaskRunner.FireAndForget(() => _host.SetListeningAsync(value), "HostLifecycle");
        }
        _flightRecorder?.RecordStep(new SetListeningStateStep(DisplayName, value));
    }

    partial void OnAutoReplyPongChanged(bool value)
    {
        _pongResponder?.SetAutoPongEnabled(NodeId, value);
    }

    [RelayCommand]
    private void RerollProfile()
    {
        if (_host == null) return;
        var name = WordPairNameGenerator.Generate();
        DisplayName = name;
        _toastService.ShowToast("Profile Updated", $"Rerolled name to '{name}'.", ToastSeverity.Informational);
    }

    [RelayCommand]
    private async Task RegisterRelayAsync()
    {
        if (_host == null || _relayCoordinator == null || _worldState == null) return;
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
        if (_host == null || _relayCoordinator == null || _worldState == null) return;
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
        if (_handshakeOrchestrator == null || !_pendingSenderId.HasValue) return;
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
        if (_handshakeOrchestrator == null || !_pendingSenderId.HasValue) return;
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
        if (_handshakeOrchestrator == null || !_pendingSenderId.HasValue) return;
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
    private async Task SendMessageAsync()
    {
        if (string.IsNullOrWhiteSpace(ComposeMessageText) || _chatCoordinator == null) return;
        var text = ComposeMessageText;
        ComposeMessageText = string.Empty;

        var peer = GetActivePeerSummary();
        if (peer == null)
        {
            _toastService.ShowToast("Send Failed", "No remote peer found. Add another persona first.", ToastSeverity.Warning);
            return;
        }

        var peerId = peer.Id;
        var success = await UiTaskRunner.RunAsync(
            () => _chatCoordinator.SendDirectMessageAsync(NodeId, peerId, text),
            category: "DirectChat",
            logSink: _logSink,
            toastService: _toastService,
            nodeName: DisplayName,
            nodeId: NodeId);

        if (success)
        {
            _flightRecorder?.RecordStep(new SendDirectMessageStep(DisplayName, peer.DisplayName, text));
        }
    }

    [RelayCommand]
    private void PublishBlindedLocator() => _toastService.ShowPlaceholder("Publish Blinded Locator", "Phase 8");

    [RelayCommand]
    private void ResolvePeerLocator() => _toastService.ShowPlaceholder("Resolve Peer Locator", "Phase 8");

    [RelayCommand]
    private void QueryNearbyPeers() => _toastService.ShowPlaceholder("Query Nearby Peers", "Phase 8");
}

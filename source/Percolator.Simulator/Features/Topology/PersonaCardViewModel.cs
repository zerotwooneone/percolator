using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ObservableCollections;
using Percolator.Domain.Common;
using Percolator.Domain.Identities.Model;
using Percolator.Domain.Identities.Ports;
using Percolator.Domain.Identities.ValueObjects;
using Percolator.Infrastructure.Persistence;
using Percolator.Simulator.Features.DirectChat;
using Percolator.Simulator.Features.Logging;
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
    private bool _autoReplenishPreKeys = false;

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

    // Compose State (CTL-PER-18)
    [ObservableProperty]
    private string _composeMessageText = string.Empty;

    public ObservableCollection<ChatMessageItemViewModel> ChatMessages { get; } = new();

    public PersonaCardViewModel(
        SimulatedNodeSummary summary,
        IStatusToastService toastService,
        ISimulatorStateBus? stateBus = null,
        DirectHandshakeOrchestrator? handshakeOrchestrator = null,
        DirectChatCoordinator? chatCoordinator = null,
        AutopilotPongResponder? pongResponder = null,
        ISelectionStateService? selectionState = null,
        ISimulatedWorldStateService? worldState = null,
        ISimulatedPersonaHost? host = null,
        ISimulatorLogSink? logSink = null,
        IFlightRecorderStateService? flightRecorder = null)
    {
        NodeId = summary.Id;
        _displayName = summary.DisplayName;
        _boundEndpoint = summary.Subtitle;
        _toastService = toastService;
        _stateBus = stateBus;
        _handshakeOrchestrator = handshakeOrchestrator;
        _chatCoordinator = chatCoordinator;
        _pongResponder = pongResponder;
        _selectionState = selectionState;
        _worldState = worldState;
        _host = host;
        _logSink = logSink;
        _flightRecorder = flightRecorder;

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
                .ThrottleLast(TimeSpan.FromMilliseconds(50))
                .Subscribe(_ => RefreshPendingHandshakes())
                .AddTo(ref Disposables);

            _stateBus.Signals
                .Where(s => (s.NodeId == NodeId || s.Details == _host.PublicIdentityId.ToString()) &&
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

            // Initial refresh
            RefreshPendingHandshakes();
            RefreshChatTranscript();
        }
    }

    private void RefreshPendingHandshakes()
    {
        if (_host == null) return;

        UiTaskRunner.FireAndForget(
            async () =>
            {
                using var scope = _host.Services.CreateScope();
                var contactRepo = scope.ServiceProvider.GetRequiredService<IPeerContactRepository>();
                var contacts = await contactRepo.GetAllForOwnerAsync(_host.PublicIdentityId);
                var pending = contacts.FirstOrDefault(c => c.State == ContactState.PendingApproval);

                UiTaskRunner.PostToUi(() =>
                {
                    if (pending != null)
                    {
                        _pendingSenderId = pending.RemotePeerId;
                        var senderHost = _worldState?.FindPersonaHostByIdentity(pending.RemotePeerId);
                        var senderName = senderHost?.DisplayName ?? pending.RemotePeerId.ToString()[..8];
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

    private void RefreshChatTranscript()
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

                UiTaskRunner.PostToUi(() =>
                {
                    if (ChatMessages.Count == transcript.Count &&
                        ChatMessages.Zip(transcript).All(pair => pair.First.Content == pair.Second.Content))
                    {
                        return; // No-op: transcript already up to date
                    }

                    if (ChatMessages.Count > 0 &&
                        transcript.Count > ChatMessages.Count &&
                        ChatMessages.Zip(transcript).All(pair => pair.First.Content == pair.Second.Content))
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
        _host?.SetListening(value);
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
    private void RegisterRelay() => _toastService.ShowPlaceholder("Register Relay", "Phase 5");

    [RelayCommand]
    private void PublishPreKeys() => _toastService.ShowPlaceholder("Publish PreKeys", "Phase 5");

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

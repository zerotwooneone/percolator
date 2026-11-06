using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Percolator.Domain.Identities.Model;
using Percolator.Domain.Identities.Ports;
using Percolator.Domain.Identities.ValueObjects;
using Percolator.Simulator.Features.DirectChat;
using Percolator.Simulator.Features.Logging;
using Percolator.Simulator.Features.Shell;
using R3;

namespace Percolator.Simulator.Features.Topology;

internal sealed partial class PersonaCardViewModel : ViewModelBase
{
    private readonly IStatusToastService _toastService;
    private readonly ISimulatorStateBus? _stateBus;
    private readonly ISimulatedPersonaHost? _host;
    private readonly DirectHandshakeOrchestrator? _handshakeOrchestrator;
    private readonly DirectChatCoordinator? _chatCoordinator;
    private readonly AutopilotPongResponder? _pongResponder;
    private readonly ISelectionStateService? _selectionState;
    private readonly ISimulatedWorldStateService? _worldState;
    private readonly ISimulatorLogSink? _logSink;

    public SimulatedNodeId NodeId { get; }

    [ObservableProperty]
    private string _displayName = string.Empty;

    [ObservableProperty]
    private string _boundEndpoint = string.Empty;

    [ObservableProperty]
    private bool _isListening = true;

    [ObservableProperty]
    private Bitmap? _identiconBitmap;

    // Feature Toggles (CTL-PER-03..06)
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
        ISimulatorLogSink? logSink = null)
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

        if (host?.IdenticonPng is not null && host.IdenticonPng.Length > 0)
        {
            using var ms = new MemoryStream(host.IdenticonPng);
            _identiconBitmap = new Bitmap(ms);
        }

        if (host is not null)
        {
            _isListening = host.IsListening.CurrentValue;
        }

        ChatMessages.Add(new ChatMessageItemViewModel
        {
            SenderName = "System",
            Content = "Ready for direct/relayed handshake.",
            TimestampText = DateTime.Now.ToString("HH:mm:ss")
        });

        if (_stateBus is not null)
        {
            _stateBus.Signals
                .Where(s => s.NodeId == NodeId)
                .Subscribe(signal =>
                {
                    Dispatcher.UIThread.Post(() => HandleSignal(signal));
                })
                .AddTo(ref Disposables);
        }

        if (_selectionState is not null)
        {
            Observable.CombineLatest(
                _selectionState.PrimarySelectedNode,
                _selectionState.SecondarySelectedNode,
                (p, s) => (p, s))
                .Subscribe(_ =>
                {
                    Dispatcher.UIThread.Post(RefreshTranscript);
                })
                .AddTo(ref Disposables);
        }

        if (_host is not null)
        {
            RefreshPendingHandshake();
            RefreshTranscript();
        }
    }

    private void HandleSignal(SimulatorStateSignal signal)
    {
        switch (signal.Category)
        {
            case SignalCategory.PendingHandshakeChanged:
                RefreshPendingHandshake();
                break;
            case SignalCategory.RatchetSessionChanged:
            case SignalCategory.ChatTranscriptChanged:
                RefreshTranscript();
                break;
        }
    }

    private void RefreshPendingHandshake()
    {
        if (_host == null || _worldState == null) return;
        _ = Task.Run(async () =>
        {
            try
            {
                using var scope = _host.Services.CreateScope();
                var contactRepo = scope.ServiceProvider.GetRequiredService<IPeerContactRepository>();
                var contacts = await contactRepo.GetAllForOwnerAsync(_host.PublicIdentityId);
                var pending = contacts.FirstOrDefault(c => c.State == ContactState.PendingApproval);

                Dispatcher.UIThread.Post(async () =>
                {
                    if (pending != null)
                    {
                        _pendingSenderId = pending.RemotePeerId;
                        var senderHost = _worldState.FindPersonaHostByIdentity(pending.RemotePeerId);
                        var senderName = senderHost?.DisplayName ?? pending.RemotePeerId.ToString();
                        PendingHandshakeText = $"Pending Handshake from {senderName}";
                        HasPendingHandshake = true;

                        if (AutoAcceptHandshakes)
                        {
                            await AcceptPendingHandshakeAsync();
                        }
                    }
                    else
                    {
                        HasPendingHandshake = false;
                        _pendingSenderId = null;
                        PendingHandshakeText = string.Empty;
                    }
                });
            }
            catch (Exception ex)
            {
                _logSink?.Log(new SimulatorLogEntry(
                    DateTimeOffset.UtcNow,
                    LogLevel.Error,
                    DisplayName,
                    NodeId,
                    "PersonaCard",
                    $"Failed to refresh pending handshake state: {ex.Message}",
                    ex.ToString()));
            }
        });
    }

    private void RefreshTranscript()
    {
        if (_host == null || _chatCoordinator == null) return;
        _ = Task.Run(async () =>
        {
            try
            {
                var peer = GetActivePeerSummary();
                if (peer == null) return;

                var transcript = await _chatCoordinator.GetTranscriptAsync(NodeId, peer.Id);

                Dispatcher.UIThread.Post(() =>
                {
                    ChatMessages.Clear();
                    if (transcript.Count == 0)
                    {
                        ChatMessages.Add(new ChatMessageItemViewModel
                        {
                            SenderName = "System",
                            Content = "Secure session active. Ready to chat.",
                            TimestampText = DateTime.Now.ToString("HH:mm:ss")
                        });
                    }
                    else
                    {
                        foreach (var msg in transcript)
                        {
                            ChatMessages.Add(msg);
                        }
                    }
                });
            }
            catch (Exception ex)
            {
                _logSink?.Log(new SimulatorLogEntry(
                    DateTimeOffset.UtcNow,
                    LogLevel.Error,
                    DisplayName,
                    NodeId,
                    "PersonaCard",
                    $"Failed to refresh chat transcript: {ex.Message}",
                    ex.ToString()));
            }
        });
    }

    private SimulatedNodeSummary? GetActivePeerSummary()
    {
        if (_selectionState == null || _worldState == null) return null;
        var primary = _selectionState.PrimarySelectedNode.CurrentValue;
        var secondary = _selectionState.SecondarySelectedNode.CurrentValue;

        if (primary != null && primary.NodeType == SimulatedNodeType.Persona && primary.Id != NodeId)
            return primary;
        if (secondary != null && secondary.NodeType == SimulatedNodeType.Persona && secondary.Id != NodeId)
            return secondary;

        return _worldState.Personas.FirstOrDefault(p => p.Id != NodeId);
    }

    partial void OnIsListeningChanged(bool value)
    {
        _host?.SetListening(value);
    }

    partial void OnAutoReplyPongChanged(bool value)
    {
        _pongResponder?.SetAutoPongEnabled(NodeId, value);
    }

    partial void OnAutoAcceptHandshakesChanged(bool value)
    {
        if (value && HasPendingHandshake)
        {
            _ = AcceptPendingHandshakeAsync();
        }
    }

    [RelayCommand]
    private void RerollProfile() => _toastService.ShowPlaceholder("Reroll Profile", "Phase 6B");

    [RelayCommand]
    private void RegisterRelay() => _toastService.ShowPlaceholder("Register Relay Mailbox", "Phase 5");

    [RelayCommand]
    private void PublishPreKeys() => _toastService.ShowPlaceholder("Publish PreKeys", "Phase 5");

    [RelayCommand]
    private async Task AcceptPendingHandshakeAsync()
    {
        if (_handshakeOrchestrator == null || !_pendingSenderId.HasValue) return;
        await _handshakeOrchestrator.AcceptHandshakeAsync(NodeId, _pendingSenderId.Value);
    }

    [RelayCommand]
    private async Task RejectPendingHandshakeAsync()
    {
        if (_handshakeOrchestrator == null || !_pendingSenderId.HasValue) return;
        await _handshakeOrchestrator.RejectHandshakeAsync(NodeId, _pendingSenderId.Value, block: false);
    }

    [RelayCommand]
    private async Task BlockPendingHandshakeAsync()
    {
        if (_handshakeOrchestrator == null || !_pendingSenderId.HasValue) return;
        await _handshakeOrchestrator.RejectHandshakeAsync(NodeId, _pendingSenderId.Value, block: true);
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

        await _chatCoordinator.SendDirectMessageAsync(NodeId, peer.Id, text);
    }

    [RelayCommand]
    private void PublishBlindedLocator() => _toastService.ShowPlaceholder("Publish Blinded Locator", "Phase 8");

    [RelayCommand]
    private void ResolvePeerLocator() => _toastService.ShowPlaceholder("Resolve Peer Locator", "Phase 8");

    [RelayCommand]
    private void QueryNearbyPeers() => _toastService.ShowPlaceholder("Query Nearby Peers", "Phase 8");
}

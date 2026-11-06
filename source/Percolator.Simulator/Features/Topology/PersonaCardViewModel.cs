using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Percolator.Simulator.Features.Shell;

namespace Percolator.Simulator.Features.Topology;

internal sealed partial class PersonaCardViewModel : ViewModelBase
{
    private readonly IStatusToastService _toastService;

    [ObservableProperty]
    private string _displayName = string.Empty;

    [ObservableProperty]
    private string _boundEndpoint = string.Empty;

    [ObservableProperty]
    private bool _isListening = true;

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

    // Compose State (CTL-PER-18)
    [ObservableProperty]
    private string _composeMessageText = string.Empty;

    public ObservableCollection<string> ChatMessages { get; } = new();

    public PersonaCardViewModel(SimulatedNodeSummary summary, IStatusToastService toastService)
    {
        _displayName = summary.DisplayName;
        _boundEndpoint = summary.Subtitle;
        _toastService = toastService;

        // Seed sample chat messages in preview mode
        ChatMessages.Add("[System] Ready for direct/relayed handshake.");
    }

    [RelayCommand]
    private void RerollProfile() => _toastService.ShowPlaceholder("Reroll Profile", "Phase 6B");

    [RelayCommand]
    private void RegisterRelay() => _toastService.ShowPlaceholder("Register Relay Mailbox", "Phase 5");

    [RelayCommand]
    private void PublishPreKeys() => _toastService.ShowPlaceholder("Publish PreKeys", "Phase 5");

    [RelayCommand]
    private void AcceptPendingHandshake() => _toastService.ShowPlaceholder("Accept Handshake", "Phase 3B");

    [RelayCommand]
    private void RejectPendingHandshake() => _toastService.ShowPlaceholder("Reject Handshake", "Phase 3B");

    [RelayCommand]
    private void BlockPendingHandshake() => _toastService.ShowPlaceholder("Block Handshake", "Phase 3B");

    [RelayCommand]
    private void SendMessage()
    {
        if (string.IsNullOrWhiteSpace(ComposeMessageText)) return;
        ChatMessages.Add($"[Me]: {ComposeMessageText}");
        ComposeMessageText = string.Empty;
        _toastService.ShowPlaceholder("Send Message Network Wire", "Phase 3B");
    }

    [RelayCommand]
    private void PublishBlindedLocator() => _toastService.ShowPlaceholder("Publish Blinded Locator", "Phase 8");

    [RelayCommand]
    private void ResolvePeerLocator() => _toastService.ShowPlaceholder("Resolve Peer Locator", "Phase 8");

    [RelayCommand]
    private void QueryNearbyPeers() => _toastService.ShowPlaceholder("Query Nearby Peers", "Phase 8");
}

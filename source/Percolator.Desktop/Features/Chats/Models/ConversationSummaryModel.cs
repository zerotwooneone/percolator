// SPECIFICATION GOVERNANCE: Read Percolator.Desktop/spec/ViewModel.spec.md before modifying this file.

namespace Percolator.Desktop.Features.Chats.Models;

using System;
using CommunityToolkit.Mvvm.ComponentModel;
using Percolator.Domain.Channels.ValueObjects;
using Percolator.Domain.Identities;

/// <summary>
/// Route classification indicating whether communication is direct peer-to-peer or routed through a relay.
/// </summary>
public enum ConversationRouteType
{
    DirectP2P,
    Relayed
}

/// <summary>
/// Delivery state for outbound connection attempts and conversation reachability.
/// </summary>
public enum ConversationDeliveryStatus
{
    None,
    Pending,
    Connected,
    Failed
}

/// <summary>
/// Presentation model representing an active conversation or pending connection in the Right Sidebar ([INV-DESKTOP-PRESENTATION-MODELS]).
/// </summary>
public sealed partial class ConversationSummaryModel : ObservableObject
{
    public ChannelId? ConversationId { get; }
    public PublicIdentityId PeerOrGroupId { get; }
    public ConversationRouteType RouteType { get; }
    public PublicIdentityId? RelayId { get; }

    [ObservableProperty]
    private string _displayName;

    [ObservableProperty]
    private string? _avatarHash;

    [ObservableProperty]
    private string _lastMessageSnippet;

    [ObservableProperty]
    private DateTimeOffset _lastActivityUtc;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUnread))]
    private int _unreadCount;

    public bool HasUnread => UnreadCount > 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsConnected))]
    private bool _isPendingOutbound;

    public bool IsConnected => !IsPendingOutbound;

    [ObservableProperty]
    private ConversationDeliveryStatus _deliveryStatus;

    public ConversationSummaryModel(
        ChannelId? conversationId,
        PublicIdentityId peerOrGroupId,
        string displayName,
        ConversationRouteType routeType,
        PublicIdentityId? relayId,
        string lastMessageSnippet,
        DateTimeOffset lastActivityUtc,
        int unreadCount = 0,
        bool isPendingOutbound = false,
        ConversationDeliveryStatus deliveryStatus = ConversationDeliveryStatus.None,
        string? avatarHash = null)
    {
        ConversationId = conversationId;
        PeerOrGroupId = peerOrGroupId;
        _displayName = string.IsNullOrWhiteSpace(displayName) ? $"Peer {peerOrGroupId.Value:N}"[..12] : displayName;
        RouteType = routeType;
        RelayId = relayId;
        _lastMessageSnippet = lastMessageSnippet;
        _lastActivityUtc = lastActivityUtc;
        _unreadCount = unreadCount;
        _isPendingOutbound = isPendingOutbound;
        _deliveryStatus = deliveryStatus;
        _avatarHash = avatarHash;
    }
}

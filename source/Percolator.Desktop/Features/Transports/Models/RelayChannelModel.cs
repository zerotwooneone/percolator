// SPECIFICATION GOVERNANCE: Read Percolator.Desktop/spec/ViewModel.spec.md before modifying this file.

namespace Percolator.Desktop.Features.Transports.Models;

using System;
using CommunityToolkit.Mvvm.ComponentModel;
using Percolator.Domain.Delivery.ValueObjects;
using Percolator.Domain.Identities;

/// <summary>
/// Connection and operational health status for a registered relay.
/// </summary>
public enum RelayConnectionStatus
{
    Disconnected,
    Connecting,
    Connected,
    Reconnecting,
    Failed
}

/// <summary>
/// Presentation model representing a configured relay (post office) in the Left Rail ([INV-DESKTOP-PRESENTATION-MODELS]).
/// </summary>
public sealed partial class RelayChannelModel : ObservableObject
{
    public PublicIdentityId RelayIdentityId { get; }
    public RelayEndpoint Endpoint { get; }

    [ObservableProperty]
    private string _displayName;

    [ObservableProperty]
    private RelayConnectionStatus _status;

    [ObservableProperty]
    private int _latencyMs;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUnread))]
    private int _unreadCount;

    /// <summary>True when one or more unread messages exist on this relay.</summary>
    public bool HasUnread => UnreadCount > 0;

    [ObservableProperty]
    private string? _folderId;

    [ObservableProperty]
    private int _displayOrder;

    [ObservableProperty]
    private bool _isLocalRelayHost;

    public RelayChannelModel(
        PublicIdentityId relayIdentityId,
        string displayName,
        RelayEndpoint endpoint,
        RelayConnectionStatus status = RelayConnectionStatus.Disconnected,
        int latencyMs = 0,
        int unreadCount = 0,
        string? folderId = null,
        int displayOrder = 0,
        bool isLocalRelayHost = false)
    {
        RelayIdentityId = relayIdentityId;
        _displayName = string.IsNullOrWhiteSpace(displayName) ? endpoint.ToString() : displayName;
        Endpoint = endpoint;
        _status = status;
        _latencyMs = latencyMs;
        _unreadCount = unreadCount;
        _folderId = folderId;
        _displayOrder = displayOrder;
        _isLocalRelayHost = isLocalRelayHost;
    }
}

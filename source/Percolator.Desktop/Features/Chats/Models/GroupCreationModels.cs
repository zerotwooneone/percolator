// SPECIFICATION GOVERNANCE: Read Percolator.Desktop/spec/ViewModel.spec.md before modifying this file.

namespace Percolator.Desktop.Features.Chats.Models;

using System;
using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;
using Percolator.Domain.Channels.ValueObjects;
using Percolator.Domain.Delivery.ValueObjects;
using Percolator.Domain.Identities;

/// <summary>
/// Parameters passed to the GroupCreationDialog via IDialogContext.
/// </summary>
public sealed record GroupCreationDialogParameter(
    PublicIdentityId ActivePersonaId,
    RelayEndpoint? InitialRelay = null);

/// <summary>
/// Result returned by the GroupCreationDialog upon closure.
/// </summary>
public sealed record GroupCreationDialogResult(
    bool IsSuccess,
    ChannelId? ChannelId = null,
    string? GroupName = null,
    RelayEndpoint? RelayEndpoint = null,
    IReadOnlyList<PublicIdentityId>? SelectedMemberIds = null)
{
    public static GroupCreationDialogResult Cancelled() => new(false);

    public static GroupCreationDialogResult Success(
        ChannelId channelId,
        string groupName,
        RelayEndpoint relayEndpoint,
        IReadOnlyList<PublicIdentityId> memberIds) =>
        new(true, channelId, groupName, relayEndpoint, memberIds);
}

/// <summary>
/// Selectable contact model for group membership creation dialog.
/// </summary>
public sealed partial class ContactSelectionItemModel : ObservableObject
{
    public PublicIdentityId IdentityId { get; }
    public string DisplayName { get; }

    [ObservableProperty]
    private bool _isSelected;

    public ContactSelectionItemModel(PublicIdentityId identityId, string displayName, bool isSelected = false)
    {
        IdentityId = identityId;
        DisplayName = displayName;
        _isSelected = isSelected;
    }
}

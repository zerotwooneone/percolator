// SPECIFICATION GOVERNANCE: Read Percolator.Desktop/spec/ViewModel.spec.md before modifying this file.

namespace Percolator.Desktop.Features.Chats.Models;

using System;
using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using Percolator.Apps.Chat.Features.Reactions;
using Percolator.Domain.Channels.ValueObjects;
using Percolator.Domain.Identities;
using Percolator.Domain.Identities.ValueObjects;

/// <summary>
/// Observable presentation model representing a chat message in the UI ([INV-DESKTOP-PRESENTATION-MODELS]).
/// Supports disappearing message expiration countdown, emoji reactions, and delivery status updates.
/// </summary>
public sealed partial class ChatMessageModel : ObservableObject
{
    public PayloadId Id { get; }
    public ChannelId ChannelId { get; }
    public PublicIdentityId AuthorId { get; }
    public string AuthorDisplayName { get; }
    public DateTimeOffset TimestampUtc { get; }
    public bool IsOutgoing { get; }

    [ObservableProperty]
    private string _content;

    [ObservableProperty]
    private MessageDeliveryStatus _deliveryStatus;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayContent))]
    private bool _isTombstone;

    [ObservableProperty]
    private DateTimeOffset? _expiresAtUtc;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayContent))]
    private bool _isExpired;

    [ObservableProperty]
    private string? _expirationRemainingText;

    [ObservableProperty]
    private IReadOnlyList<ReactionGroupModel> _reactions = Array.Empty<ReactionGroupModel>();

    [ObservableProperty]
    private IReadOnlyList<string> _attachmentFileNames = Array.Empty<string>();

    public string DisplayContent => IsTombstone || IsExpired
        ? "This message was deleted"
        : Content;

    public ChatMessageModel(
        PayloadId id,
        ChannelId channelId,
        PublicIdentityId authorId,
        string authorDisplayName,
        string content,
        DateTimeOffset timestampUtc,
        bool isOutgoing,
        MessageDeliveryStatus deliveryStatus = MessageDeliveryStatus.Sent,
        bool isTombstone = false,
        DateTimeOffset? expiresAtUtc = null,
        IReadOnlyList<ReactionGroupModel>? reactions = null,
        IReadOnlyList<string>? attachmentFileNames = null)
    {
        Id = id;
        ChannelId = channelId;
        AuthorId = authorId;
        AuthorDisplayName = string.IsNullOrWhiteSpace(authorDisplayName)
            ? $"Peer {authorId.Value:N}"[..12]
            : authorDisplayName;
        _content = content;
        TimestampUtc = timestampUtc;
        IsOutgoing = isOutgoing;
        _deliveryStatus = deliveryStatus;
        _isTombstone = isTombstone;
        _expiresAtUtc = expiresAtUtc;
        _reactions = reactions ?? Array.Empty<ReactionGroupModel>();
        _attachmentFileNames = attachmentFileNames ?? Array.Empty<string>();
    }

    /// <summary>
    /// Evaluates message expiration against the current virtualized time.
    /// Updates <see cref="IsExpired"/> and <see cref="ExpirationRemainingText"/> accordingly.
    /// </summary>
    public void UpdateExpiration(DateTimeOffset nowUtc)
    {
        if (ExpiresAtUtc == null || IsTombstone || IsExpired)
        {
            ExpirationRemainingText = null;
            return;
        }

        var remaining = ExpiresAtUtc.Value - nowUtc;
        if (remaining <= TimeSpan.Zero)
        {
            IsExpired = true;
            IsTombstone = true;
            ExpirationRemainingText = null;
        }
        else if (remaining.TotalHours >= 24)
        {
            ExpirationRemainingText = $"{(int)remaining.TotalDays}d";
        }
        else if (remaining.TotalMinutes >= 60)
        {
            ExpirationRemainingText = $"{(int)remaining.TotalHours}h";
        }
        else if (remaining.TotalSeconds >= 60)
        {
            ExpirationRemainingText = $"{(int)remaining.TotalMinutes}m";
        }
        else
        {
            ExpirationRemainingText = $"{(int)remaining.TotalSeconds}s";
        }
    }

    /// <summary>
    /// Produces an immutable snapshot for thread-safe caching and export ([INV-DESKTOP-PRESENTATION-MODELS]).
    /// </summary>
    public ChatMessageSnapshot ToSnapshot()
    {
        return new ChatMessageSnapshot(
            Id,
            ChannelId,
            AuthorId,
            AuthorDisplayName,
            Content,
            TimestampUtc,
            IsOutgoing,
            DeliveryStatus,
            IsTombstone || IsExpired,
            ExpiresAtUtc,
            Reactions.ToList(),
            AttachmentFileNames.ToList());
    }
}

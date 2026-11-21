// SPECIFICATION GOVERNANCE: Read Percolator.Desktop/spec/ViewModel.spec.md before modifying this file.

namespace Percolator.Desktop.Features.Chats.Models;

using System;
using System.Collections.Generic;
using Percolator.Apps.Chat.Features.Reactions;
using Percolator.Domain.Channels.ValueObjects;
using Percolator.Domain.Identities;
using Percolator.Domain.Identities.ValueObjects;

/// <summary>
/// Message delivery status for outgoing chat bubbles.
/// </summary>
public enum MessageDeliveryStatus
{
    Sending,
    Sent,
    Delivered,
    Read,
    Failed
}

/// <summary>
/// Immutable snapshot record representing a chat message for lock-free background reads,
/// caching, and export ([INV-DESKTOP-PRESENTATION-MODELS]).
/// </summary>
public sealed record ChatMessageSnapshot(
    PayloadId Id,
    ChannelId ChannelId,
    PublicIdentityId AuthorId,
    string AuthorDisplayName,
    string Content,
    DateTimeOffset TimestampUtc,
    bool IsOutgoing,
    MessageDeliveryStatus DeliveryStatus,
    bool IsTombstone,
    DateTimeOffset? ExpiresAtUtc,
    IReadOnlyList<ReactionGroupModel> Reactions,
    IReadOnlyList<string> AttachmentFileNames);

/// <summary>
/// Presentation model for an emoji reaction group on a message.
/// </summary>
public sealed record ReactionGroupModel(
    Emoji Emoji,
    int Count,
    bool HasLocalUserReacted);

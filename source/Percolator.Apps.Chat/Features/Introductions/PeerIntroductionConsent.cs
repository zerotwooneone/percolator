using System;
using Percolator.Domain.Channels.ValueObjects;
using Percolator.Domain.Common;
using Percolator.Domain.Identities;
using Percolator.Domain.Identities.ValueObjects;

namespace Percolator.Apps.Chat.Features.Introductions;

public sealed class PeerIntroductionConsent
{
    public string RequestId { get; }
    public ChannelId ChannelId { get; }
    public PublicIdentityId LocalIdentityId { get; }
    public PublicIdentityId RemotePeerId { get; }
    public PublicIdentityId CandidatePeerId { get; }
    public string CandidateDisplayName { get; }
    public string? Note { get; }
    public IntroductionDirection Direction { get; }
    public IntroductionStatus Status { get; private set; }
    public ChannelId? ForwardChannelId { get; }
    public PeerInvitation? Invitation { get; private set; }
    public string? DeclineReason { get; private set; }
    public DateTimeOffset CreatedAtUtc { get; }
    public DateTimeOffset? RespondedAtUtc { get; private set; }

    public PeerIntroductionConsent(
        string requestId,
        ChannelId channelId,
        PublicIdentityId localIdentityId,
        PublicIdentityId remotePeerId,
        PublicIdentityId candidatePeerId,
        string candidateDisplayName,
        string? note,
        IntroductionDirection direction,
        IntroductionStatus status,
        DateTimeOffset createdAtUtc,
        ChannelId? forwardChannelId = null,
        PeerInvitation? invitation = null,
        string? declineReason = null,
        DateTimeOffset? respondedAtUtc = null)
    {
        if (string.IsNullOrWhiteSpace(requestId))
            throw new ArgumentException("RequestId cannot be empty.", nameof(requestId));
        if (!channelId.IsValid)
            throw new ArgumentException("ChannelId cannot be empty.", nameof(channelId));
        if (!localIdentityId.IsValid)
            throw new ArgumentException("LocalIdentityId cannot be empty.", nameof(localIdentityId));
        if (!remotePeerId.IsValid)
            throw new ArgumentException("RemotePeerId cannot be empty.", nameof(remotePeerId));
        if (!candidatePeerId.IsValid)
            throw new ArgumentException("CandidatePeerId cannot be empty.", nameof(candidatePeerId));

        RequestId = requestId;
        ChannelId = channelId;
        LocalIdentityId = localIdentityId;
        RemotePeerId = remotePeerId;
        CandidatePeerId = candidatePeerId;
        CandidateDisplayName = candidateDisplayName ?? string.Empty;
        Note = note;
        Direction = direction;
        Status = status;
        CreatedAtUtc = createdAtUtc;
        ForwardChannelId = forwardChannelId;
        Invitation = invitation;
        DeclineReason = declineReason;
        RespondedAtUtc = respondedAtUtc;
    }

    public DomainResult Allow(PeerInvitation invitation, DateTimeOffset respondedAt)
    {
        ArgumentNullException.ThrowIfNull(invitation);

        if (Status != IntroductionStatus.Pending)
        {
            return DomainResult.Failure(new DomainError(
                "INVALID_STATE_TRANSITION",
                $"Cannot allow introduction in status '{Status}'. Only Pending introductions can be allowed."));
        }

        Invitation = invitation;
        Status = IntroductionStatus.Allowed;
        RespondedAtUtc = respondedAt;
        return DomainResult.Success();
    }

    public DomainResult Decline(string? reason, DateTimeOffset respondedAt)
    {
        if (Status != IntroductionStatus.Pending)
        {
            return DomainResult.Failure(new DomainError(
                "INVALID_STATE_TRANSITION",
                $"Cannot decline introduction in status '{Status}'. Only Pending introductions can be declined."));
        }

        DeclineReason = reason;
        Status = IntroductionStatus.Declined;
        RespondedAtUtc = respondedAt;
        return DomainResult.Success();
    }

    public DomainResult MarkForwarded()
    {
        if (Status != IntroductionStatus.Allowed)
        {
            return DomainResult.Failure(new DomainError(
                "INVALID_STATE_TRANSITION",
                $"Cannot mark introduction as forwarded from status '{Status}'. Must be in Allowed status."));
        }

        Status = IntroductionStatus.Forwarded;
        return DomainResult.Success();
    }

    public DomainResult MarkConnected()
    {
        if (Status != IntroductionStatus.Pending && Status != IntroductionStatus.Allowed && Status != IntroductionStatus.Forwarded)
        {
            return DomainResult.Failure(new DomainError(
                "INVALID_STATE_TRANSITION",
                $"Cannot mark introduction as connected from status '{Status}'."));
        }

        Status = IntroductionStatus.Connected;
        return DomainResult.Success();
    }
}

using System;
using Percolator.Domain.Channels.ValueObjects;
using Percolator.Domain.Identities;
using Percolator.Domain.Identities.ValueObjects;
using Percolator.PluginSdk.Events;

namespace Percolator.Apps.Chat.Features.Introductions;

public sealed record IntroductionConsentRequestedApplicationEvent(
    string RequestId,
    ChannelId ChannelId,
    PublicIdentityId RequesterPeerId,
    PublicIdentityId CandidatePeerId,
    string CandidateDisplayName,
    string? Note,
    DateTimeOffset OccurredOnUtc) : IApplicationEvent
{
    public Guid EventId { get; } = Guid.NewGuid();
}

public sealed record IntroductionConsentResolvedApplicationEvent(
    string RequestId,
    ChannelId ChannelId,
    PublicIdentityId ResponderPeerId,
    bool Allowed,
    string? DeclineReason,
    PeerInvitation? Invitation,
    DateTimeOffset OccurredOnUtc) : IApplicationEvent
{
    public Guid EventId { get; } = Guid.NewGuid();
}

public sealed record PeerIntroductionCardReceivedApplicationEvent(
    string RequestId,
    ChannelId ChannelId,
    PublicIdentityId IntroducerPeerId,
    PublicIdentityId IntroducedPeerId,
    string IntroducedDisplayName,
    PeerInvitation Invitation,
    string? Note,
    DateTimeOffset OccurredOnUtc) : IApplicationEvent
{
    public Guid EventId { get; } = Guid.NewGuid();
}

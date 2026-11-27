namespace Percolator.Apps.Chat.Features.Introductions;

using Percolator.Domain.Channels.ValueObjects;
using Percolator.Domain.Identities;

public sealed record RequestIntroductionConsentCommand(
    ChannelId ChannelId,
    PublicIdentityId RequesterId,
    PublicIdentityId CandidatePeerId,
    string CandidateDisplayName,
    ChannelId? ForwardChannelId = null,
    string? Note = null);

public sealed record RespondToIntroductionConsentCommand(
    string RequestId,
    bool Allowed,
    string? DeclineReason = null);

public sealed record ForwardIntroductionCardCommand(
    string RequestId,
    ChannelId ForwardChannelId,
    string? Note = null);

public sealed record AcceptPeerIntroductionCardCommand(
    string RequestId,
    PublicIdentityId LocalIdentityId);


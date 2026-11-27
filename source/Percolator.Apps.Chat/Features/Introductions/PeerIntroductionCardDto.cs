using Percolator.Domain.Identities;

namespace Percolator.Apps.Chat.Features.Introductions;

public sealed record PeerIntroductionCardDto(
    string RequestId,
    PublicIdentityId IntroducedPeerId,
    string IntroducedDisplayName,
    PeerInvitationWireDto Invitation,
    string? Note,
    ulong TimestampUnixMs);

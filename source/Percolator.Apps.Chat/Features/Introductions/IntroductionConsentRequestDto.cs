using Percolator.Domain.Identities;

namespace Percolator.Apps.Chat.Features.Introductions;

public sealed record IntroductionConsentRequestDto(
    string RequestId,
    PublicIdentityId CandidatePeerId,
    string CandidateDisplayName,
    string? Note,
    ulong TimestampUnixMs);

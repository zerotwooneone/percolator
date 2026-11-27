namespace Percolator.Apps.Chat.Features.Introductions;

public sealed record IntroductionConsentResponseDto(
    string RequestId,
    bool Allowed,
    PeerInvitationWireDto? Invitation,
    string? DeclineReason,
    ulong RespondedAtUnixMs);

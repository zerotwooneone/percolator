using Percolator.Identity;

namespace Percolator.Application.Sessions;

public sealed record PendingInboundSnapshot(
    Guid PendingSessionId,
    Guid RequestCorrelationId,
    Guid PeerId,
    string PeerName,
    string? InviterFingerprintHex,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? ExpiresAtUtc,
    bool IsRelayed,
    Guid? RelayPeerId,
    string? RelayPeerName,
    string? RelayEndpoint,
    SelfId SelfIdentityId
);

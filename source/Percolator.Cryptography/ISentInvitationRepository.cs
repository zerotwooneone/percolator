using Percolator.Cryptography.Primitives;

namespace Percolator.Cryptography;

public interface ISentInvitationRepository
{
    Task UpsertAsync(SentInvitation invitation, CryptoSelfId selfIdentityId, CancellationToken cancellationToken = default);
    Task<SentInvitation?> TryGetAsync(RequestCorrelationId requestCorrelationId, CryptoSelfId selfIdentityId, CancellationToken cancellationToken = default);
    Task SetInviteRouteAsync(RequestCorrelationId requestCorrelationId, CryptoSelfId selfIdentityId, InviteRouteKind routeKind, PeerId? relayHostPeerId, CancellationToken cancellationToken = default);
    Task DeleteAsync(RequestCorrelationId requestCorrelationId, CryptoSelfId selfIdentityId, CancellationToken cancellationToken = default);
    IAsyncEnumerable<SentInvitation> EnumerateUnexpiredAsync(CryptoSelfId selfIdentityId, DateTimeOffset nowUtc, CancellationToken cancellationToken = default);
    IAsyncEnumerable<SentInvitation> EnumerateExpiredAsync(CryptoSelfId selfIdentityId, DateTimeOffset nowUtc, CancellationToken cancellationToken = default);
}

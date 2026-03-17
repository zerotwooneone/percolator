using Percolator.Cryptography.Primitives;

namespace Percolator.Cryptography;

public interface ISentInvitationRepository
{
    Task UpsertAsync(SentInvitation invitation, CancellationToken cancellationToken = default);
    Task<SentInvitation?> TryGetAsync(RequestCorrelationId requestCorrelationId, CancellationToken cancellationToken = default);
    Task SetInviteRouteAsync(RequestCorrelationId requestCorrelationId, InviteRouteKind routeKind, PeerId? relayHostPeerId, CancellationToken cancellationToken = default);
    Task DeleteAsync(RequestCorrelationId requestCorrelationId, CancellationToken cancellationToken = default);
    IAsyncEnumerable<SentInvitation> EnumerateExpiredAsync(DateTimeOffset nowUtc, CancellationToken cancellationToken = default);
}

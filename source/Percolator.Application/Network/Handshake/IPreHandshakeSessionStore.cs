using Percolator.Identity;
using Percolator.Network;

namespace Percolator.Application.Network.Handshake
{
    /// <summary>
    /// Stores initiator pre-handshake state prior to session id assignment by the responder.
    /// </summary>
    public interface IPreHandshakeSessionStore
    {
        Task SaveAsync(PreHandshakeRecord record, CancellationToken cancellationToken);

        /// <summary>
        /// Enumerate Pending records scoped to the self identity.
        /// </summary>
        IAsyncEnumerable<PreHandshakeRecord> EnumeratePendingAsync(NetworkSelfId selfIdentityId, CancellationToken cancellationToken);

        /// <summary>
        /// Returns the most recent, non-expired prehandshake record for the given self identity, or null if none.
        /// </summary>
        Task<PreHandshakeRecord?> TryGetMostRecentAsync(NetworkSelfId selfIdentityId, CancellationToken cancellationToken);

        Task DeleteAsync(long recordId, NetworkSelfId selfIdentityId, CancellationToken cancellationToken);

        Task PurgeExpiredAsync(NetworkSelfId selfIdentityId, CancellationToken cancellationToken);
    }

    /// <summary>
    /// Minimal record for pre-handshake persistence (initiator-side), sufficient for slow-path finalize.
    /// </summary>
    public sealed record PreHandshakeRecord(
        long Id,
        uint SelfIdentityId,
        PublicIdentityId RecipientPublicIdentityId,
        Guid LocalRequestId,
        byte[] InitiatorEphemeralPrivateKey,
        byte[] InitialRootKey,
        DateTimeOffset CreatedAtUtc,
        DateTimeOffset? ExpiresAtUtc,
        byte[] RemoteIdentityKeySpki
    );
}

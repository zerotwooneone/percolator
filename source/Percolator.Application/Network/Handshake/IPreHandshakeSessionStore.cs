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
        IAsyncEnumerable<PreHandshakeRecord> EnumeratePendingAsync(int selfIdentityId, CancellationToken cancellationToken);

        /// <summary>
        /// Returns the most recent, non-expired prehandshake record for the given self identity, or null if none.
        /// </summary>
        Task<PreHandshakeRecord?> TryGetMostRecentAsync(int selfIdentityId, CancellationToken cancellationToken);

        Task DeleteAsync(long recordId, int selfIdentityId, CancellationToken cancellationToken);

        Task PurgeExpiredAsync(int selfIdentityId, CancellationToken cancellationToken);
    }

    /// <summary>
    /// Minimal record for pre-handshake persistence (initiator-side), sufficient for slow-path finalize.
    /// </summary>
    public sealed record PreHandshakeRecord(
        long Id,
        int SelfIdentityId,
        byte[] RecipientPublicKeyHash,
        Guid LocalRequestId,
        byte[] InitiatorEphemeralPrivateKey,
        byte[] InitialRootKey,
        DateTimeOffset CreatedAtUtc,
        DateTimeOffset? ExpiresAtUtc,
        byte[] RemoteIdentityKeySpki
    );
}

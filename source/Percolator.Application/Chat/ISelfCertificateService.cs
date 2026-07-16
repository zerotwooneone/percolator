using Percolator.Chat.GroupLedger;
using Percolator.Chat.GroupMembership;

namespace Percolator.Application.Chat;

/// <summary>
/// Service for managing delivery certificates for local self identities.
/// Certificates are generated JIT and stored keyed by SelfId until they expire.
/// </summary>
public interface ISelfCertificateService
{
    /// <summary>
    /// Gets an existing valid certificate for the given SelfId, or generates a new one if none exists or it has expired.
    /// </summary>
    /// <param name="selfId">The local self identity ID</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>A valid delivery certificate, or null if the self identity does not exist</returns>
    Task<DeliveryCertificate?> GenerateValidCertAsync(ChatSelfId selfId, CancellationToken ct);
}

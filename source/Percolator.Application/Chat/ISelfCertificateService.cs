using Percolator.Chat.GroupLedger;
using Percolator.Chat.GroupMembership;
using Percolator.Cryptography;
using Percolator.Identity;
using PublicIdentityId = Percolator.Identity.PublicIdentityId;

namespace Percolator.Application.Chat;

/// <summary>
/// Represents authentication headers for relay connections.
/// </summary>
public record RelayAuthenticationHeaders(string PublicIdentityId, string Timestamp, string Signature);

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
    /// <param name="publicIdentityId">The public identity ID</param>
    /// <param name="fingerprint">The identity fingerprint</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>A valid delivery certificate, or null if the self identity does not exist</returns>
    Task<DeliveryCertificate?> GenerateValidCertAsync(ChatSelfId selfId, PublicIdentityId publicIdentityId, RatchetIdentityKey fingerprint, CancellationToken ct);

    /// <summary>
    /// Gets authentication headers for relay connection.
    /// </summary>
    /// <param name="selfId">The local self identity ID</param>
    /// <param name="publicIdentityId">The public identity ID</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>Authentication headers, or null if the self identity does not exist</returns>
    Task<RelayAuthenticationHeaders?> GetRelayAuthenticationHeadersAsync(ChatSelfId selfId, PublicIdentityId publicIdentityId, CancellationToken ct);
}

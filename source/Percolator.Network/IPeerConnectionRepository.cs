namespace Percolator.Network;

/// <summary>
/// Defines the contract for a repository that manages persistent peer connection information.
/// </summary>
public interface IPeerConnectionRepository
{
    /// <summary>
    /// Retrieves a peer's connection information by their stable identifier.
    /// </summary>
    /// <param name="peerId">The ID of the peer.</param>
    /// <returns>The peer's connection information, or null if not found.</returns>
    Task<PeerConnection?> GetByIdAsync(PeerId peerId);

    /// <summary>
    /// Creates or updates a peer's connection information.
    /// </summary>
    /// <param name="peerConnection">The peer connection object to save.</param>
    Task SaveAsync(PeerConnection peerConnection);

    Task<PeerConnection?> GetByDirectMessage(DirectMessagePublicKey directMessagePublicKey);

    /// <summary>
    /// Gets a peer connection by their TLS certificate.
    /// </summary>
    /// <param name="tlsCertificate">The peer's TLS certificate.</param>
    /// <returns>The peer connection, if found.</returns>
    Task<PeerConnection?> GetByTlsCertificateAsync(TlsCertificate tlsCertificate);

    /// <summary>
    /// Updates a peer's connection details with their direct message public key.
    /// </summary>
    /// <param name="peerId">The ID of the peer.</param>
    /// <param name="publicKey">The direct message public key to update.</param>
    Task UpdateDirectMessagePublicKeyAsync(PeerId peerId, DirectMessagePublicKey publicKey);
}

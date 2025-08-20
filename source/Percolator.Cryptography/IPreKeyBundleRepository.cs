using Percolator.Cryptography.Primitives;

namespace Percolator.Cryptography;

/// <summary>
/// Manages the storage and retrieval of pre-key bundles, which are used to establish secure sessions asynchronously.
/// </summary>
public interface IPreKeyBundleRepository
{
    /// <summary>
    /// Stores a collection of pre-key bundles for a specific peer.
    /// If bundles for the peer already exist, they should be replaced.
    /// </summary>
    /// <param name="peerId">The unique identifier of the peer who owns the bundles.</param>
    /// <param name="bundles">The collection of pre-key bundles to store.</param>
    Task StoreBundlesAsync(PeerId peerId, IEnumerable<PreKeyBundle> bundles);

    /// <summary>
    /// Atomically retrieves and removes a single pre-key bundle for a specified peer.
    /// This operation is destructive: it consumes a one-time pre-key, which cannot be retrieved again.
    /// </summary>
    /// <param name="peerId">The unique identifier of the peer for whom to retrieve the bundle.</param>
    /// <returns>
    /// A <see cref="PreKeyBundle"/> for the peer, or null if no valid bundle is available.
    /// </returns>
    Task<PreKeyBundle?> PopBundleAsync(PeerId peerId);
}

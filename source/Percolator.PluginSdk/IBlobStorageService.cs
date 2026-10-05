using Percolator.Domain.Common;

namespace Percolator.PluginSdk;

public interface IBlobStorageService
{
    /// <summary>
    /// Strips EXIF metadata, encrypts (chunked STREAM AEAD), and stages/transfers media:
    /// - 1:1 Direct (no relay): stages ciphertext in local chunk store for direct peer pull.
    /// - 1:1 Relayed or GroupChannel: uploads once to the channel's designated relay.
    /// Returns a compact BlobReference suitable for embedding in an E2EE chat message.
    /// </summary>
    ValueTask<DomainResult<BlobUploadResult>> UploadBlobAsync(
        BlobUploadRequest request,
        IProgress<TransferProgress>? progress = null,
        CancellationToken ct = default);

    /// <summary>
    /// Downloads, verifies chunk authentication tags, and decrypts the media payload.
    /// Uses local disk cache if previously downloaded. Supports byte-range resumption.
    /// </summary>
    ValueTask<DomainResult<BlobDownloadResult>> DownloadBlobAsync(
        BlobDownloadRequest request,
        IProgress<TransferProgress>? progress = null,
        CancellationToken ct = default);

    /// <summary>
    /// Checks whether the blob is currently available in the local cache without network I/O.
    /// </summary>
    bool IsCachedLocally(BlobId blobId);

    /// <summary>
    /// Removes an expired or deleted blob from local disk cache.
    /// </summary>
    ValueTask DeleteLocalCacheAsync(BlobId blobId, CancellationToken ct = default);
}

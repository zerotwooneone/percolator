using Percolator.Domain.Common;
using Percolator.PluginSdk;

namespace Percolator.Apps.Chat.Tests.TestDoubles;

/// <summary>
/// No-op blob storage for tests that do not exercise out-of-band attachment flows.
/// </summary>
public sealed class FakeBlobStorageService : IBlobStorageService
{
    public ValueTask<DomainResult<BlobUploadResult>> UploadBlobAsync(
        BlobUploadRequest request,
        IProgress<TransferProgress>? progress = null,
        CancellationToken ct = default)
    {
        return ValueTask.FromResult(DomainResult<BlobUploadResult>.Failure(
            new DomainError("BLOB_STORAGE_UNAVAILABLE", "Fake blob storage does not persist uploads.")));
    }

    public ValueTask<DomainResult<BlobDownloadResult>> DownloadBlobAsync(
        BlobDownloadRequest request,
        IProgress<TransferProgress>? progress = null,
        CancellationToken ct = default)
    {
        return ValueTask.FromResult(DomainResult<BlobDownloadResult>.Failure(
            new DomainError("BLOB_STORAGE_UNAVAILABLE", "Fake blob storage has no stored blobs.")));
    }

    public bool IsCachedLocally(BlobId blobId) => false;

    public ValueTask DeleteLocalCacheAsync(BlobId blobId, CancellationToken ct = default)
    {
        return ValueTask.CompletedTask;
    }
}

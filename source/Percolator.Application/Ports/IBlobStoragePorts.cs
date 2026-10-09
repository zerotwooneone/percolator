using Percolator.Domain.Channels.ValueObjects;
using Percolator.Domain.Common;
using Percolator.Domain.Identities.ValueObjects;
using Percolator.PluginSdk;

namespace Percolator.Application2.Ports;

public interface IRelayBlobClient
{
    ValueTask<DomainResult<string>> UploadBlobAsync(
        Uri relayUri,
        BlobId blobId,
        Stream ciphertextStream,
        ChannelId channelId,
        IProgress<TransferProgress>? progress = null,
        CancellationToken ct = default);

    ValueTask<DomainResult<Stream>> DownloadBlobAsync(
        Uri relayUri,
        BlobId blobId,
        IProgress<TransferProgress>? progress = null,
        CancellationToken ct = default);

    ValueTask<DomainResult> DeleteBlobAsync(
        Uri relayUri,
        BlobId blobId,
        CancellationToken ct = default);
}

public interface IPeerStreamBlobClient
{
    ValueTask<DomainResult<Stream>> RequestBlobFromPeerAsync(
        PublicIdentityId peerId,
        BlobId blobId,
        IProgress<TransferProgress>? progress = null,
        CancellationToken ct = default);
}

public interface ILocalChunkStore
{
    ValueTask<bool> HasBlobAsync(BlobId blobId, CancellationToken ct = default);

    ValueTask<Stream> OpenReadAsync(BlobId blobId, CancellationToken ct = default);

    ValueTask SaveBlobAsync(BlobId blobId, Stream contentStream, CancellationToken ct = default);

    ValueTask DeleteBlobAsync(BlobId blobId, CancellationToken ct = default);
}

public interface IChannelRelayResolver
{
    ValueTask<Uri?> ResolveRelayForChannelAsync(ChannelId channelId, CancellationToken ct = default);
}

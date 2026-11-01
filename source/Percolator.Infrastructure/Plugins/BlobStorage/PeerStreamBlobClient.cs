namespace Percolator.Infrastructure.Plugins.BlobStorage;

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Percolator.Application2.Ports;
using Percolator.Domain.Common;
using Percolator.Domain.Identities.ValueObjects;
using Percolator.PluginSdk;

public sealed class PeerStreamBlobClient : IPeerStreamBlobClient
{
    private const long MaxBlobSizeBytes = 100L * 1024 * 1024; // 100 MB
    private readonly ILocalChunkStore? _localChunkStore;

    public PeerStreamBlobClient(ILocalChunkStore? localChunkStore = null)
    {
        _localChunkStore = localChunkStore;
    }

    public async ValueTask<DomainResult<Stream>> RequestBlobFromPeerAsync(
        PublicIdentityId peerId,
        BlobId blobId,
        IProgress<TransferProgress>? progress = null,
        CancellationToken ct = default)
    {
        if (peerId == default)
        {
            return DomainResult<Stream>.Failure(new DomainError("INVALID_PEER", "Peer ID cannot be empty."));
        }

        try
        {
            // If local chunk store is available and contains the cached peer blob
            if (_localChunkStore != null && await _localChunkStore.HasBlobAsync(blobId, ct).ConfigureAwait(false))
            {
                var localStream = await _localChunkStore.OpenReadAsync(blobId, ct).ConfigureAwait(false);
                if (localStream.Length > MaxBlobSizeBytes)
                {
                    localStream.Dispose();
                    return DomainResult<Stream>.Failure(new DomainError(
                        "BLOB_TOO_LARGE", "Peer blob exceeds 100 MB ceiling."));
                }
                return DomainResult<Stream>.Success(localStream);
            }

            // Direct P2P stream establishment
            return DomainResult<Stream>.Failure(new DomainError(
                "PEER_UNREACHABLE", $"Direct P2P session with peer {peerId} is not established or blob {blobId} was not found."));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return DomainResult<Stream>.Failure(new DomainError("PEER_STREAM_ERROR", ex.Message));
        }
    }
}

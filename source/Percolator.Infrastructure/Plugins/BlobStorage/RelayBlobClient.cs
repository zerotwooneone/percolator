namespace Percolator.Infrastructure.Plugins.BlobStorage;

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Google.Protobuf;
using Grpc.Core;
using Grpc.Net.Client;
using Percolator.Application2.Ports;
using Percolator.Domain.Channels.ValueObjects;
using Percolator.Domain.Common;
using Percolator.Infrastructure.Protos;
using Percolator.PluginSdk;

public sealed class RelayBlobClient : IRelayBlobClient
{
    private const long MaxBlobSizeBytes = 100L * 1024 * 1024; // 100 MB
    private const int ChunkSize = 64 * 1024; // 64 KB
    private const long InMemoryThresholdBytes = 1024 * 1024; // 1 MB

    private readonly Func<Uri, UnauthenticatedDeliveryService.UnauthenticatedDeliveryServiceClient>? _clientFactory;

    public RelayBlobClient(Func<Uri, UnauthenticatedDeliveryService.UnauthenticatedDeliveryServiceClient>? clientFactory = null)
    {
        _clientFactory = clientFactory;
    }

    public async ValueTask<DomainResult<string>> UploadBlobAsync(
        Uri relayUri,
        BlobId blobId,
        Stream ciphertextStream,
        ChannelId channelId,
        IProgress<TransferProgress>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(relayUri);
        ArgumentNullException.ThrowIfNull(ciphertextStream);

        if (ciphertextStream.CanSeek && ciphertextStream.Length > MaxBlobSizeBytes)
        {
            return DomainResult<string>.Failure(new DomainError(
                "BLOB_TOO_LARGE", $"Blob ciphertext size ({ciphertextStream.Length} bytes) exceeds 100 MB ceiling."));
        }

        try
        {
            var client = GetClient(relayUri);
            using var call = client.UploadBlindBlob(cancellationToken: ct);

            byte[] buffer = new byte[ChunkSize];
            uint chunkIndex = 0;
            long totalUploaded = 0;
            long totalLength = ciphertextStream.CanSeek ? ciphertextStream.Length : -1;
            int bytesRead;

            byte[] blobBytes = Convert.FromHexString(blobId.HexDigest);
            var blobIdByteString = ByteString.CopyFrom(blobBytes);

            while ((bytesRead = await ciphertextStream.ReadAsync(buffer.AsMemory(0, ChunkSize), ct).ConfigureAwait(false)) > 0)
            {
                totalUploaded += bytesRead;
                if (totalUploaded > MaxBlobSizeBytes)
                {
                    return DomainResult<string>.Failure(new DomainError(
                        "BLOB_TOO_LARGE", "Stream exceeded 100 MB ceiling during upload."));
                }

                bool isFinal = ciphertextStream.CanSeek && ciphertextStream.Position >= ciphertextStream.Length;
                var chunk = new BlindBlobChunkProto
                {
                    BlobId = blobIdByteString,
                    ChunkIndex = chunkIndex++,
                    ChunkData = ByteString.CopyFrom(buffer, 0, bytesRead),
                    IsFinalChunk = isFinal
                };

                await call.RequestStream.WriteAsync(chunk).ConfigureAwait(false);

                if (progress != null && totalLength > 0)
                {
                    progress.Report(new TransferProgress
                    {
                        BytesTransferred = totalUploaded,
                        TotalBytes = totalLength
                    });
                }

                if (isFinal) break;
            }

            await call.RequestStream.CompleteAsync().ConfigureAwait(false);
            var response = await call.ResponseAsync.ConfigureAwait(false);

            if (!response.Success)
            {
                return DomainResult<string>.Failure(new DomainError(
                    "UPLOAD_FAILED", response.ErrorMessage ?? "Relay rejected blob upload."));
            }

            return DomainResult<string>.Success(response.BlobUri);
        }
        catch (RpcException ex)
        {
            return DomainResult<string>.Failure(new DomainError("RPC_ERROR", ex.Status.Detail ?? ex.Message));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return DomainResult<string>.Failure(new DomainError("TRANSPORT_ERROR", ex.Message));
        }
    }

    public async ValueTask<DomainResult<Stream>> DownloadBlobAsync(
        Uri relayUri,
        BlobId blobId,
        IProgress<TransferProgress>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(relayUri);

        try
        {
            var client = GetClient(relayUri);
            byte[] blobBytes = Convert.FromHexString(blobId.HexDigest);
            var request = new BlindBlobDownloadRequestProto
            {
                BlobId = ByteString.CopyFrom(blobBytes)
            };

            using var call = client.DownloadBlindBlob(request, cancellationToken: ct);
            Stream destinationStream = new MemoryStream();
            long totalBytes = 0;

            while (await call.ResponseStream.MoveNext(ct).ConfigureAwait(false))
            {
                var chunk = call.ResponseStream.Current;
                totalBytes += chunk.ChunkData.Length;

                if (totalBytes > MaxBlobSizeBytes)
                {
                    destinationStream.Dispose();
                    return DomainResult<Stream>.Failure(new DomainError(
                        "BLOB_TOO_LARGE", "Downloaded blob exceeded 100 MB ceiling."));
                }

                if (destinationStream is MemoryStream memStream && totalBytes > InMemoryThresholdBytes)
                {
                    string tempPath = Path.Combine(Path.GetTempPath(), $"blob_dl_{Guid.NewGuid():N}.tmp");
                    var fileStream = new FileStream(
                        tempPath,
                        FileMode.CreateNew,
                        FileAccess.ReadWrite,
                        FileShare.None,
                        bufferSize: ChunkSize,
                        FileOptions.DeleteOnClose);

                    memStream.Position = 0;
                    await memStream.CopyToAsync(fileStream, ct).ConfigureAwait(false);
                    memStream.Dispose();
                    destinationStream = fileStream;
                }

                await destinationStream.WriteAsync(chunk.ChunkData.Memory, ct).ConfigureAwait(false);

                if (progress != null)
                {
                    progress.Report(new TransferProgress
                    {
                        BytesTransferred = totalBytes,
                        TotalBytes = -1
                    });
                }

                if (chunk.IsFinalChunk) break;
            }

            destinationStream.Position = 0;
            return DomainResult<Stream>.Success(destinationStream);
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.NotFound)
        {
            return DomainResult<Stream>.Failure(new DomainError("NOT_FOUND", $"Blob {blobId} not found on relay."));
        }
        catch (RpcException ex)
        {
            return DomainResult<Stream>.Failure(new DomainError("RPC_ERROR", ex.Status.Detail ?? ex.Message));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return DomainResult<Stream>.Failure(new DomainError("TRANSPORT_ERROR", ex.Message));
        }
    }

    public async ValueTask<DomainResult> DeleteBlobAsync(
        Uri relayUri,
        BlobId blobId,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(relayUri);

        try
        {
            var client = GetClient(relayUri);
            byte[] blobBytes = Convert.FromHexString(blobId.HexDigest);
            var request = new BlindBlobDeleteRequestProto
            {
                BlobId = ByteString.CopyFrom(blobBytes)
            };

            var response = await client.DeleteBlindBlobAsync(request, cancellationToken: ct);
            if (!response.Success)
            {
                return DomainResult.Failure(new DomainError(
                    "DELETE_FAILED", response.ErrorMessage ?? "Failed to delete blob from relay."));
            }

            return DomainResult.Success();
        }
        catch (RpcException ex)
        {
            return DomainResult.Failure(new DomainError("RPC_ERROR", ex.Status.Detail ?? ex.Message));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return DomainResult.Failure(new DomainError("TRANSPORT_ERROR", ex.Message));
        }
    }

    private UnauthenticatedDeliveryService.UnauthenticatedDeliveryServiceClient GetClient(Uri relayUri)
    {
        if (_clientFactory != null)
        {
            return _clientFactory(relayUri);
        }

        var channel = GrpcChannel.ForAddress(relayUri);
        return new UnauthenticatedDeliveryService.UnauthenticatedDeliveryServiceClient(channel);
    }
}

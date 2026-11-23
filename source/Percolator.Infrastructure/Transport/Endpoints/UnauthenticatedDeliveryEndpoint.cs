namespace Percolator.Infrastructure.Transport.Endpoints;

using System;
using System.IO;
using System.Security.Cryptography;
using System.Threading.Tasks;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Percolator.Application.Ports;
using Percolator.Domain.Common;
using Percolator.Domain.Delivery.Hosting;
using Percolator.Domain.Delivery.Ports;
using Percolator.Domain.Delivery.ValueObjects;
using Percolator.Domain.Identities.ValueObjects;
using Percolator.Domain.Security.ValueObjects;
using Percolator.Infrastructure.Protos;
using Percolator.PluginSdk;

public sealed class UnauthenticatedDeliveryEndpoint : UnauthenticatedDeliveryService.UnauthenticatedDeliveryServiceBase
{
    private readonly IRelayProfileRepository _profileRepository;
    private readonly ILocalChunkStore _chunkStore;
    private readonly IRelayMailboxCapabilityStore _capabilityStore;
    private readonly IRelayHostingPolicyProvider _policyProvider;

    public UnauthenticatedDeliveryEndpoint(
        IRelayProfileRepository profileRepository,
        ILocalChunkStore chunkStore,
        IRelayMailboxCapabilityStore capabilityStore,
        IRelayHostingPolicyProvider policyProvider)
    {
        _profileRepository = profileRepository ?? throw new ArgumentNullException(nameof(profileRepository));
        _chunkStore = chunkStore ?? throw new ArgumentNullException(nameof(chunkStore));
        _capabilityStore = capabilityStore ?? throw new ArgumentNullException(nameof(capabilityStore));
        _policyProvider = policyProvider ?? throw new ArgumentNullException(nameof(policyProvider));
    }

    public override async Task<EnqueueMailboxResponseProto> EnqueueMailbox(EnqueueMailboxRequestProto request, ServerCallContext context)
    {
        var policy = _policyProvider.GetPolicy();
        if (request.DeliveryToken.IsEmpty)
        {
            throw new RpcException(new Status(StatusCode.PermissionDenied, "DeliveryToken is required for unauthenticated mailbox delivery."));
        }

        if (request.Envelope == null || request.Envelope.EncryptedPackage.Length > policy.MaxMailboxBytes)
        {
            throw new RpcException(new Status(StatusCode.ResourceExhausted, "Envelope size exceeds relay mailbox quota."));
        }

        if (request.RecipientRoutingToken.Length != 16 || request.DeliveryToken.Length != 32)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "Routing token (16B) and delivery token (32B) required."));
        }

        var routingToken = BlindedRoutingToken.FromBytes(request.RecipientRoutingToken.ToByteArray());
        var deliveryToken = DeliveryToken.FromBytes(request.DeliveryToken.ToByteArray());
        var now = DateTimeOffset.UtcNow;
        var envelope = new MailboxEnvelope(
            EnvelopeId.New(),
            routingToken,
            request.Envelope.ToByteArray(),
            now,
            now.Add(policy.EnvelopeTtl));

        // Enforces constant-time DeliveryToken verification AND cumulative per-mailbox byte + envelope-count quotas
        var result = await _capabilityStore.EnqueueEnvelopeAsync(routingToken, deliveryToken, envelope, policy.MaxMailboxBytes, context.CancellationToken);
        if (result.IsFailure)
        {
            var statusCode = result.Error.Code == "MAILBOX_QUOTA_EXCEEDED"
                ? StatusCode.ResourceExhausted
                : StatusCode.PermissionDenied;
            throw new RpcException(new Status(statusCode, result.Error.Description));
        }

        return new EnqueueMailboxResponseProto { Success = true };
    }

    public override async Task<ProfileFetchResponseProto> GetProfile(ProfileFetchRequestProto request, ServerCallContext context)
    {
        var policy = _policyProvider.GetPolicy();
        if (!policy.IsFeatureEnabled(RelayFeatureFlags.Profiles))
        {
            throw new RpcException(new Status(StatusCode.PermissionDenied, "Profiles feature is disabled on this relay."));
        }

        if (request.BlindedLocator.Length != 32)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "Blinded locator must be exactly 32 bytes."));
        }

        try
        {
            var locator = BlindedProfileLocator.FromSpan(request.BlindedLocator.Span);
            var slot = await _profileRepository.GetSlotAsync(locator, context.CancellationToken);

            if (slot == null)
            {
                return new ProfileFetchResponseProto { Found = false };
            }

            return new ProfileFetchResponseProto
            {
                Found = true,
                Nonce = ByteString.CopyFrom(slot.Package.Nonce),
                Ciphertext = ByteString.CopyFrom(slot.Package.Ciphertext),
                Revision = slot.Revision,
                ExpiresAtUtc = Timestamp.FromDateTimeOffset(slot.ExpiresAtUtc)
            };
        }
        catch (RpcException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new RpcException(new Status(StatusCode.Internal, $"GetProfile handler failed: {ex.GetType().Name} - {ex.Message}"));
        }
    }

    public override async Task<ProfileUploadResponseProto> UploadProfile(ProfileUploadRequestProto request, ServerCallContext context)
    {
        var policy = _policyProvider.GetPolicy();
        if (!policy.IsFeatureEnabled(RelayFeatureFlags.Profiles))
        {
            throw new RpcException(new Status(StatusCode.PermissionDenied, "Profiles feature is disabled on this relay."));
        }

        if (request.BlindedLocator.Length != 32)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "Blinded locator must be exactly 32 bytes."));
        }

        if (request.WriteToken.Length != 32)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "Write token must be exactly 32 bytes."));
        }

        if (request.Nonce.Length != 12)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "Nonce must be 12 bytes."));
        }

        if (request.Ciphertext.Length != RelayHostedProfileSlot.ExactPackageSizeBytes)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, $"Ciphertext package must be exactly {RelayHostedProfileSlot.ExactPackageSizeBytes} bytes."));
        }

        try
        {
            var locator = BlindedProfileLocator.FromSpan(request.BlindedLocator.Span);
            var writeToken = ProfileWriteToken.FromSpan(request.WriteToken.Span);
            var package = new ProfileCiphertextPackage(request.Nonce.ToByteArray(), request.Ciphertext.ToByteArray());

            var expiresAtUtc = DateTimeOffset.UtcNow.AddDays(30);
            var createResult = RelayHostedProfileSlot.Create(locator, writeToken, package, request.Revision, expiresAtUtc);
            if (createResult.IsFailure)
            {
                throw new RpcException(new Status(StatusCode.InvalidArgument, createResult.Error!.Description));
            }

            var saveResult = await _profileRepository.SaveSlotAsync(createResult.Value!, context.CancellationToken);
            if (saveResult.IsFailure)
            {
                throw new RpcException(new Status(StatusCode.Aborted, saveResult.Error!.Description));
            }

            return new ProfileUploadResponseProto
            {
                Success = true
            };
        }
        catch (RpcException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new RpcException(new Status(StatusCode.Internal, $"UploadProfile handler failed: {ex.GetType().Name} - {ex.Message}"));
        }
    }

    public override async Task<ProfileDeleteResponseProto> DeleteProfile(ProfileDeleteRequestProto request, ServerCallContext context)
    {
        var policy = _policyProvider.GetPolicy();
        if (!policy.IsFeatureEnabled(RelayFeatureFlags.Profiles))
        {
            throw new RpcException(new Status(StatusCode.PermissionDenied, "Profiles feature is disabled on this relay."));
        }

        if (request.BlindedLocator.Length != 32 || request.WriteToken.Length != 32)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "Locator and write token must be 32 bytes."));
        }

        var locator = BlindedProfileLocator.FromSpan(request.BlindedLocator.Span);
        var writeToken = ProfileWriteToken.FromSpan(request.WriteToken.Span);

        var result = await _profileRepository.DeleteSlotAsync(locator, writeToken, context.CancellationToken);
        return new ProfileDeleteResponseProto
        {
            Success = result.IsSuccess
        };
    }

    public override async Task DownloadBlindBlob(
        BlindBlobDownloadRequestProto request,
        IServerStreamWriter<BlindBlobChunkProto> responseStream,
        ServerCallContext context)
    {
        var policy = _policyProvider.GetPolicy();
        if (!policy.IsFeatureEnabled(RelayFeatureFlags.Blobs))
        {
            throw new RpcException(new Status(StatusCode.PermissionDenied, "Blobs feature is disabled on this relay."));
        }

        if (request.BlobId.Length != 32)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "Blob ID must be exactly 32 bytes (SHA-256)."));
        }

        var blobId = BlobId.FromSha256(request.BlobId.ToByteArray());
        bool exists = await _chunkStore.HasBlobAsync(blobId, context.CancellationToken);
        if (!exists)
        {
            throw new RpcException(new Status(StatusCode.NotFound, $"Blob {blobId} not found."));
        }

        using var stream = await _chunkStore.OpenReadAsync(blobId, context.CancellationToken);
        byte[] buffer = new byte[64 * 1024];
        uint chunkIndex = 0;
        int bytesRead;

        while ((bytesRead = await stream.ReadAsync(buffer, context.CancellationToken)) > 0)
        {
            var chunk = new BlindBlobChunkProto
            {
                ChunkIndex = chunkIndex++,
                ChunkData = ByteString.CopyFrom(buffer, 0, bytesRead),
                IsFinalChunk = stream.Position >= stream.Length,
                BlobId = request.BlobId
            };

            await responseStream.WriteAsync(chunk);
        }
    }

    public override async Task<BlindBlobUploadResponseProto> UploadBlindBlob(
        IAsyncStreamReader<BlindBlobChunkProto> requestStream,
        ServerCallContext context)
    {
        var policy = _policyProvider.GetPolicy();
        if (!policy.IsFeatureEnabled(RelayFeatureFlags.Blobs))
        {
            throw new RpcException(new Status(StatusCode.PermissionDenied, "Blobs feature is disabled on this relay."));
        }

        var stagingDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Percolator", "Staging");
        Directory.CreateDirectory(stagingDir);
        string tempPath = Path.Combine(stagingDir, $"blob_up_{Guid.NewGuid():N}.tmp");
        byte[]? blobIdBytes = null;
        long totalBytes = 0;
        const long maxBlobSize = 100 * 1024 * 1024; // 100 MB

        try
        {
            using (var tempFile = new FileStream(
                tempPath,
                FileMode.CreateNew,
                FileAccess.ReadWrite,
                FileShare.None,
                bufferSize: 64 * 1024,
                FileOptions.DeleteOnClose))
            {
                using var incrementalHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

                while (await requestStream.MoveNext(context.CancellationToken))
                {
                    var chunk = requestStream.Current;
                    if (chunk.BlobId != null && !chunk.BlobId.IsEmpty)
                    {
                        if (chunk.BlobId.Length != 32)
                        {
                            throw new RpcException(new Status(StatusCode.InvalidArgument, "Blob ID must be exactly 32 bytes (SHA-256)."));
                        }

                        if (blobIdBytes == null)
                        {
                            blobIdBytes = chunk.BlobId.ToByteArray();
                        }
                        else if (!CryptographicOperations.FixedTimeEquals(blobIdBytes, chunk.BlobId.Span))
                        {
                            throw new RpcException(new Status(StatusCode.InvalidArgument, "Conflicting Blob ID declared across stream chunks."));
                        }
                    }

                    totalBytes += chunk.ChunkData.Length;
                    if (totalBytes > maxBlobSize)
                    {
                        throw new RpcException(new Status(StatusCode.InvalidArgument, "Stream exceeds 100 MB limit."));
                    }

                    incrementalHash.AppendData(chunk.ChunkData.Span);
                    await tempFile.WriteAsync(chunk.ChunkData.Memory, context.CancellationToken);
                    if (chunk.IsFinalChunk)
                    {
                        break;
                    }
                }

                if (blobIdBytes == null)
                {
                    throw new RpcException(new Status(StatusCode.InvalidArgument, "Missing or invalid Blob ID in stream chunks."));
                }

                Span<byte> actualHash = stackalloc byte[32];
                if (!incrementalHash.TryGetHashAndReset(actualHash, out int bytesWritten) || bytesWritten != 32)
                {
                    throw new RpcException(new Status(StatusCode.Internal, "Failed to compute SHA-256 hash."));
                }

                if (!CryptographicOperations.FixedTimeEquals(actualHash, blobIdBytes))
                {
                    throw new RpcException(new Status(StatusCode.InvalidArgument, "Uploaded blob SHA-256 digest does not match declared BlobId."));
                }

                tempFile.Position = 0;
                var blobId = BlobId.FromSha256(blobIdBytes);
                await _chunkStore.SaveBlobAsync(blobId, tempFile, context.CancellationToken);

                return new BlindBlobUploadResponseProto
                {
                    Success = true,
                    BlobUri = $"/blobs/{blobId}"
                };
            }
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                try
                {
                    File.Delete(tempPath);
                }
                catch (IOException)
                {
                    // Best-effort cleanup for temp file
                }
                catch (UnauthorizedAccessException)
                {
                    // Best-effort cleanup for temp file
                }
            }
        }
    }
}

namespace Percolator.Infrastructure.Transport.Endpoints;

using System;
using System.IO;
using System.Security.Cryptography;
using System.Threading.Tasks;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Percolator.Application2.Ingress;
using Percolator.Application2.Ports;
using Percolator.Domain.Common;
using Percolator.Domain.Delivery.Hosting;
using Percolator.Domain.Delivery.Ports;
using Percolator.Domain.Delivery.ValueObjects;
using Percolator.Domain.Identities.ValueObjects;
using Percolator.Domain.Security.ValueObjects;
using Percolator.Infrastructure.Protos;
using Percolator.Infrastructure.Transport;
using Percolator.PluginSdk;

public sealed class UnauthenticatedDeliveryEndpoint : UnauthenticatedDeliveryService.UnauthenticatedDeliveryServiceBase
{
    private readonly IRelayProfileRepository _profileRepository;
    private readonly IRelayPreKeyRepository _preKeyRepository;
    private readonly ILocalChunkStore _chunkStore;
    private readonly IRelayMailboxCapabilityStore _capabilityStore;
    private readonly IIngressFilterService? _ingressFilter;
    private readonly ISealedEnvelopeUnwrapper? _unwrapper;
    private readonly IInboundIngressPipeline? _ingressPipeline;

    public UnauthenticatedDeliveryEndpoint(
        IRelayProfileRepository profileRepository,
        IRelayPreKeyRepository preKeyRepository,
        ILocalChunkStore chunkStore,
        IRelayMailboxCapabilityStore? capabilityStore = null,
        IIngressFilterService? ingressFilter = null,
        ISealedEnvelopeUnwrapper? unwrapper = null,
        IInboundIngressPipeline? ingressPipeline = null)
    {
        _profileRepository = profileRepository ?? throw new ArgumentNullException(nameof(profileRepository));
        _preKeyRepository = preKeyRepository ?? throw new ArgumentNullException(nameof(preKeyRepository));
        _chunkStore = chunkStore ?? throw new ArgumentNullException(nameof(chunkStore));
        _capabilityStore = capabilityStore ?? new InMemoryRelayMailboxCapabilityStore();
        _ingressFilter = ingressFilter;
        _unwrapper = unwrapper;
        _ingressPipeline = ingressPipeline;
    }

    public override async Task<DeliveryAckProto> DeliverDirect(SealedEnvelopeProto request, ServerCallContext context)
    {
        if (request.EncryptedPackage.IsEmpty)
        {
            return new DeliveryAckProto
            {
                Success = false,
                ErrorMessage = "Encrypted package cannot be empty."
            };
        }

        if (_unwrapper != null)
        {
            var unwrapResult = _unwrapper.UnwrapDirect(request.EncryptedPackage.Memory);

            if (!unwrapResult.IsSuccess)
            {
                return new DeliveryAckProto
                {
                    Success = false,
                    ErrorMessage = unwrapResult.Error!.Description
                };
            }

            var envelope = unwrapResult.Value!;
            if (_ingressFilter != null)
            {
                var filterResult = await _ingressFilter.CheckIngressAllowedAsync(
                    envelope.RecipientIdentityId,
                    envelope.SenderIdentityId,
                    context.CancellationToken);

                if (filterResult.IsFailure)
                {
                    return new DeliveryAckProto
                    {
                        Success = false,
                        ErrorMessage = filterResult.Error!.Description
                    };
                }
            }

            if (_ingressPipeline != null)
            {
                var ingressResult = await _ingressPipeline.ProcessInboundAsync(envelope, context.CancellationToken);
                if (ingressResult.IsFailure && ingressResult.Error.Code != "EXPIRED_OR_DUPLICATE_MESSAGE")
                {
                    return new DeliveryAckProto
                    {
                        Success = false,
                        ErrorMessage = ingressResult.Error.Description
                    };
                }
            }
        }

        return new DeliveryAckProto
        {
            Success = true
        };
    }

    public override async Task<EnqueueAckProto> EnqueueMailbox(SealedEnvelopeProto request, ServerCallContext context)
    {
        if (request.RecipientRoutingToken.IsEmpty || request.DeliveryToken.IsEmpty)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "Recipient routing token and delivery token are required."));
        }

        bool isAuthorized = await _capabilityStore.VerifyDeliveryTokenAsync(
            request.RecipientRoutingToken.Memory,
            request.DeliveryToken.Memory,
            context.CancellationToken);

        if (!isAuthorized)
        {
            throw new RpcException(new Status(StatusCode.PermissionDenied, "Invalid delivery token capability."));
        }

        var queued = new QueuedSealedEnvelopeProto
        {
            QueueId = Guid.NewGuid().ToString("N"),
            EnvelopePayload = request.ToByteString(),
            EnqueuedAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow)
        };

        await _capabilityStore.EnqueueEnvelopeAsync(request.RecipientRoutingToken.Memory, queued, context.CancellationToken);

        return new EnqueueAckProto
        {
            Accepted = true,
            ExpiresAtUtc = request.ExpiresAtUtc
        };
    }

    public override async Task<FetchPreKeyBundleResponse> FetchPreKeyBundle(FetchPreKeyBundleRequest request, ServerCallContext context)
    {
        if (request.RecipientRoutingToken.IsEmpty || request.DeliveryToken.IsEmpty)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "Routing token and delivery token are required."));
        }

        // Anonymous capability gating: verify delivery token in constant time
        bool isAuthorized = await _capabilityStore.VerifyDeliveryTokenAsync(
            request.RecipientRoutingToken.Memory,
            request.DeliveryToken.Memory,
            context.CancellationToken);

        if (!isAuthorized)
        {
            // Reject without consuming OPK
            throw new RpcException(new Status(StatusCode.PermissionDenied, "Invalid delivery token capability."));
        }

        var (ownerId, deviceId) = await _capabilityStore.GetOwnerAsync(request.RecipientRoutingToken.Memory, context.CancellationToken);
        if (!ownerId.HasValue || !deviceId.HasValue)
        {
            throw new RpcException(new Status(StatusCode.NotFound, "No pre-key bundle associated with recipient routing token."));
        }

        var bundle = await _preKeyRepository.GetAsync(ownerId.Value, deviceId.Value, context.CancellationToken);
        if (bundle == null)
        {
            throw new RpcException(new Status(StatusCode.NotFound, "Pre-key bundle not found for identity."));
        }

        // Consume one-time prekey
        var consumeResult = bundle.ConsumeBundle();
        if (consumeResult.IsFailure)
        {
            throw new RpcException(new Status(StatusCode.NotFound, "Unable to consume pre-key bundle."));
        }

        await _preKeyRepository.SaveAsync(bundle, context.CancellationToken);
        var consumed = consumeResult.Value!;

        var response = new FetchPreKeyBundleResponse
        {
            IdentityKey = ByteString.CopyFrom(consumed.IdentityKey.Span),
            SignedPrekey = ByteString.CopyFrom(consumed.SignedPreKey.Span),
            PrekeySignature = ByteString.CopyFrom(consumed.SignedPreKeySignature.Span),
            SignedPrekeyId = 1
        };

        if (consumed.OneTimePreKey != null)
        {
            response.OneTimePrekey = ByteString.CopyFrom(consumed.OneTimePreKey.Span);
            response.OneTimePrekeyId = consumed.OneTimePreKeyId;
        }

        return response;
    }

    public override async Task<ProfileFetchResponseProto> GetProfile(ProfileFetchRequestProto request, ServerCallContext context)
    {
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
        string tempPath = Path.Combine(Path.GetTempPath(), $"blob_up_{Guid.NewGuid():N}.tmp");
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

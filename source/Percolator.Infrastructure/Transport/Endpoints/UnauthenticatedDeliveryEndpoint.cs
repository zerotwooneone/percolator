namespace Percolator.Infrastructure.Transport.Endpoints;

using System;
using System.IO;
using System.Threading.Tasks;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
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

    public UnauthenticatedDeliveryEndpoint(
        IRelayProfileRepository profileRepository,
        IRelayPreKeyRepository preKeyRepository,
        ILocalChunkStore chunkStore,
        IRelayMailboxCapabilityStore? capabilityStore = null,
        IIngressFilterService? ingressFilter = null,
        ISealedEnvelopeUnwrapper? unwrapper = null)
    {
        _profileRepository = profileRepository ?? throw new ArgumentNullException(nameof(profileRepository));
        _preKeyRepository = preKeyRepository ?? throw new ArgumentNullException(nameof(preKeyRepository));
        _chunkStore = chunkStore ?? throw new ArgumentNullException(nameof(chunkStore));
        _capabilityStore = capabilityStore ?? new InMemoryRelayMailboxCapabilityStore();
        _ingressFilter = ingressFilter;
        _unwrapper = unwrapper;
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

        if (_unwrapper != null && _ingressFilter != null)
        {
            var unwrapResult = _unwrapper.Unwrap(new MailboxEnvelope(
                EnvelopeId.New(),
                BlindedRoutingToken.New(),
                request.EncryptedPackage.Memory,
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow.AddDays(1)));

            if (unwrapResult.IsSuccess)
            {
                var envelope = unwrapResult.Value!;
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

        if (request.Ciphertext.Length != RelayHostedProfileSlot.ExactPackageSizeBytes)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, $"Ciphertext package must be exactly {RelayHostedProfileSlot.ExactPackageSizeBytes} bytes."));
        }

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
                IsFinalChunk = stream.Position >= stream.Length
            };

            await responseStream.WriteAsync(chunk);
        }
    }
}

using Percolator.Application2.Ports;
using Percolator.Domain.Channels.Ports;
using Percolator.Domain.Common;
using Percolator.PluginSdk;

namespace Percolator.Application2.Services;

public sealed class BlobStorageService : IBlobStorageService
{
    private readonly IChannelRepository _channelRepo;
    private readonly IChannelRelayResolver _relayResolver;
    private readonly IStreamRegistry _streamRegistry;
    private readonly IBlobCryptoService _cryptoService;
    private readonly IRelayBlobClient _relayBlobClient;
    private readonly IPeerStreamBlobClient _peerStreamBlobClient;
    private readonly ILocalChunkStore _localChunkStore;
    private readonly IDateTimeProvider _timeProvider;

    public BlobStorageService(
        IChannelRepository channelRepo,
        IChannelRelayResolver relayResolver,
        IStreamRegistry streamRegistry,
        IBlobCryptoService cryptoService,
        IRelayBlobClient relayBlobClient,
        IPeerStreamBlobClient peerStreamBlobClient,
        ILocalChunkStore localChunkStore,
        IDateTimeProvider timeProvider)
    {
        _channelRepo = channelRepo ?? throw new ArgumentNullException(nameof(channelRepo));
        _relayResolver = relayResolver ?? throw new ArgumentNullException(nameof(relayResolver));
        _streamRegistry = streamRegistry ?? throw new ArgumentNullException(nameof(streamRegistry));
        _cryptoService = cryptoService ?? throw new ArgumentNullException(nameof(cryptoService));
        _relayBlobClient = relayBlobClient ?? throw new ArgumentNullException(nameof(relayBlobClient));
        _peerStreamBlobClient = peerStreamBlobClient ?? throw new ArgumentNullException(nameof(peerStreamBlobClient));
        _localChunkStore = localChunkStore ?? throw new ArgumentNullException(nameof(localChunkStore));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    public async ValueTask<DomainResult<BlobUploadResult>> UploadBlobAsync(
        BlobUploadRequest request,
        IProgress<TransferProgress>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        // 1. Resolve channel topology
        var directChannel = await _channelRepo.GetDirectByIdAsync(request.ChannelId, ct).ConfigureAwait(false);
        var groupChannel = directChannel == null
            ? await _channelRepo.GetGroupByIdAsync(request.ChannelId, ct).ConfigureAwait(false)
            : null;

        if (directChannel == null && groupChannel == null)
        {
            return DomainResult<BlobUploadResult>.Failure(new DomainError(
                "CHANNEL_NOT_FOUND", $"Channel {request.ChannelId} does not exist."));
        }

        // 2. Encrypt & package out-of-band (EXIF stripped, bucket padded, 64 KB STREAM chunks)
        var packageResult = await _cryptoService.EncryptAndPackageAsync(
            request.ContentStream, request.MimeType, ct).ConfigureAwait(false);

        if (!packageResult.IsSuccess)
        {
            return DomainResult<BlobUploadResult>.Failure(packageResult.Error!);
        }

        var package = packageResult.Value!;
        var blobId = BlobId.FromSha256(package.CiphertextSha256);
        var ttl = request.TimeToLive ?? TimeSpan.FromDays(14);
        var expiresAtUtc = _timeProvider.UtcNow.Add(ttl);

        // 3. Topology-based routing
        if (directChannel != null)
        {
            var relayUri = await _relayResolver.ResolveRelayForChannelAsync(request.ChannelId, ct).ConfigureAwait(false);

            if (relayUri == null)
            {
                // Topology A: 1:1 Direct (No Relay) -> Online-only direct P2P
                if (!_streamRegistry.HasActiveStream(directChannel.RemotePeerId))
                {
                    return DomainResult<BlobUploadResult>.Failure(new DomainError(
                        "DIRECT_PEER_OFFLINE",
                        "Media cannot be sent because the recipient is offline in a direct un-relayed channel."));
                }

                // Stage ciphertext in local chunk store for recipient to pull
                await _localChunkStore.SaveBlobAsync(blobId, package.CiphertextStream, ct).ConfigureAwait(false);

                var directRef = new BlobReference
                {
                    Id = blobId,
                    CiphertextSha256 = package.CiphertextSha256,
                    EncryptionKey = package.EncryptionKey,
                    BaseNonce = package.BaseNonce,
                    PlaintextSizeBytes = package.PlaintextSize,
                    CiphertextSizeBytes = package.CiphertextSize,
                    MimeType = request.MimeType,
                    ExpiresAtUtc = expiresAtUtc,
                    StorageMode = BlobStorageMode.DirectP2P,
                    RelayEndpointUri = null,
                    BlurHash = request.BlurHash,
                    InlineThumbnail = request.InlineThumbnail,
                    Waveform = request.Waveform,
                    Width = request.Width,
                    Height = request.Height,
                    DurationSeconds = request.DurationSeconds
                };

                return DomainResult<BlobUploadResult>.Success(
                    new BlobUploadResult(directRef, BlobStorageMode.DirectP2P));
            }
            else
            {
                // Topology B: 1:1 Relayed -> Upload to channel's designated relay
                var uploadResult = await _relayBlobClient.UploadBlobAsync(
                    relayUri, blobId, package.CiphertextStream, request.ChannelId, progress, ct).ConfigureAwait(false);

                if (!uploadResult.IsSuccess)
                {
                    return DomainResult<BlobUploadResult>.Failure(uploadResult.Error!);
                }

                // Also stage in local chunk store for fast local replay
                package.CiphertextStream.Position = 0;
                await _localChunkStore.SaveBlobAsync(blobId, package.CiphertextStream, ct).ConfigureAwait(false);

                var relayedRef = new BlobReference
                {
                    Id = blobId,
                    CiphertextSha256 = package.CiphertextSha256,
                    EncryptionKey = package.EncryptionKey,
                    BaseNonce = package.BaseNonce,
                    PlaintextSizeBytes = package.PlaintextSize,
                    CiphertextSizeBytes = package.CiphertextSize,
                    MimeType = request.MimeType,
                    ExpiresAtUtc = expiresAtUtc,
                    StorageMode = BlobStorageMode.ChannelRelay,
                    RelayEndpointUri = uploadResult.Value!,
                    BlurHash = request.BlurHash,
                    InlineThumbnail = request.InlineThumbnail,
                    Waveform = request.Waveform,
                    Width = request.Width,
                    Height = request.Height,
                    DurationSeconds = request.DurationSeconds
                };

                return DomainResult<BlobUploadResult>.Success(
                    new BlobUploadResult(relayedRef, BlobStorageMode.ChannelRelay));
            }
        }
        else
        {
            // Topology C: Group Channel -> Upload once to group's common rendezvous relay
            var commonRelayUri = await _relayResolver.ResolveRelayForChannelAsync(request.ChannelId, ct).ConfigureAwait(false);
            if (commonRelayUri == null)
            {
                return DomainResult<BlobUploadResult>.Failure(new DomainError(
                    "GROUP_RELAY_UNAVAILABLE",
                    $"Common relay for group channel {request.ChannelId} is not configured or unavailable."));
            }

            var groupUploadResult = await _relayBlobClient.UploadBlobAsync(
                commonRelayUri, blobId, package.CiphertextStream, request.ChannelId, progress, ct).ConfigureAwait(false);

            if (!groupUploadResult.IsSuccess)
            {
                return DomainResult<BlobUploadResult>.Failure(groupUploadResult.Error!);
            }

            // Cache in local chunk store
            package.CiphertextStream.Position = 0;
            await _localChunkStore.SaveBlobAsync(blobId, package.CiphertextStream, ct).ConfigureAwait(false);

            var groupRef = new BlobReference
            {
                Id = blobId,
                CiphertextSha256 = package.CiphertextSha256,
                EncryptionKey = package.EncryptionKey,
                BaseNonce = package.BaseNonce,
                PlaintextSizeBytes = package.PlaintextSize,
                CiphertextSizeBytes = package.CiphertextSize,
                MimeType = request.MimeType,
                ExpiresAtUtc = expiresAtUtc,
                StorageMode = BlobStorageMode.ChannelRelay,
                RelayEndpointUri = groupUploadResult.Value!,
                BlurHash = request.BlurHash,
                InlineThumbnail = request.InlineThumbnail,
                Waveform = request.Waveform,
                Width = request.Width,
                Height = request.Height,
                DurationSeconds = request.DurationSeconds
            };

            return DomainResult<BlobUploadResult>.Success(
                new BlobUploadResult(groupRef, BlobStorageMode.ChannelRelay));
        }
    }

    public async ValueTask<DomainResult<BlobDownloadResult>> DownloadBlobAsync(
        BlobDownloadRequest request,
        IProgress<TransferProgress>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        Stream ciphertextStream;

        // 1. Fast path: check local chunk cache first
        if (await _localChunkStore.HasBlobAsync(request.Reference.Id, ct).ConfigureAwait(false))
        {
            ciphertextStream = await _localChunkStore.OpenReadAsync(request.Reference.Id, ct).ConfigureAwait(false);
        }
        else
        {
            // 2. Fetch based on storage mode
            if (request.Reference.StorageMode == BlobStorageMode.ChannelRelay)
            {
                if (string.IsNullOrWhiteSpace(request.Reference.RelayEndpointUri) ||
                    !Uri.TryCreate(request.Reference.RelayEndpointUri, UriKind.Absolute, out var relayUri))
                {
                    return DomainResult<BlobDownloadResult>.Failure(new DomainError(
                        "INVALID_RELAY_URI", "BlobReference does not contain a valid relay endpoint URI."));
                }

                var downloadResult = await _relayBlobClient.DownloadBlobAsync(
                    relayUri, request.Reference.Id, progress, ct).ConfigureAwait(false);

                if (!downloadResult.IsSuccess)
                {
                    return DomainResult<BlobDownloadResult>.Failure(downloadResult.Error!);
                }

                ciphertextStream = downloadResult.Value!;
                ciphertextStream.Position = 0;
                await _localChunkStore.SaveBlobAsync(request.Reference.Id, ciphertextStream, ct).ConfigureAwait(false);
                ciphertextStream.Position = 0;
            }
            else
            {
                return DomainResult<BlobDownloadResult>.Failure(new DomainError(
                    "P2P_DOWNLOAD_NOT_FOUND",
                    "Direct P2P blob is not in local cache and remote peer session resolution is required."));
            }
        }

        // 3. Decrypt STREAM AEAD payload
        var decryptResult = await _cryptoService.DecryptStreamAsync(
            ciphertextStream,
            request.Reference.EncryptionKey,
            request.Reference.BaseNonce,
            request.Reference.PlaintextSizeBytes,
            ct).ConfigureAwait(false);

        if (!decryptResult.IsSuccess)
        {
            return DomainResult<BlobDownloadResult>.Failure(decryptResult.Error!);
        }

        return DomainResult<BlobDownloadResult>.Success(new BlobDownloadResult(
            decryptResult.Value!,
            request.Reference.MimeType,
            request.Reference.PlaintextSizeBytes));
    }

    public bool IsCachedLocally(BlobId blobId)
    {
        return _localChunkStore.HasBlobAsync(blobId).GetAwaiter().GetResult();
    }

    public async ValueTask DeleteLocalCacheAsync(BlobId blobId, CancellationToken ct = default)
    {
        await _localChunkStore.DeleteBlobAsync(blobId, ct).ConfigureAwait(false);
    }
}

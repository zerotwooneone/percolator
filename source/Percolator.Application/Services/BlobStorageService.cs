using Percolator.Application.Ports;
using Percolator.Domain.Channels.Ports;
using Percolator.Domain.Common;
using Percolator.PluginSdk;

namespace Percolator.Application.Services;

public sealed class BlobStorageService : IBlobStorageService
{
    private readonly IChannelRepository _channelRepo;
    private readonly IChannelRelayResolver _relayResolver;
    private readonly IBlobCryptoService _cryptoService;
    private readonly IRelayBlobClient _relayBlobClient;
    private readonly ILocalChunkStore _localChunkStore;
    private readonly IDateTimeProvider _timeProvider;

    public BlobStorageService(
        IChannelRepository channelRepo,
        IChannelRelayResolver relayResolver,
        IBlobCryptoService cryptoService,
        IRelayBlobClient relayBlobClient,
        ILocalChunkStore localChunkStore,
        IDateTimeProvider timeProvider)
    {
        _channelRepo = channelRepo ?? throw new ArgumentNullException(nameof(channelRepo));
        _relayResolver = relayResolver ?? throw new ArgumentNullException(nameof(relayResolver));
        _cryptoService = cryptoService ?? throw new ArgumentNullException(nameof(cryptoService));
        _relayBlobClient = relayBlobClient ?? throw new ArgumentNullException(nameof(relayBlobClient));
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

        var relayUri = await _relayResolver.ResolveRelayForChannelAsync(request.ChannelId, ct).ConfigureAwait(false);
        if (relayUri == null)
        {
            return DomainResult<BlobUploadResult>.Failure(new DomainError(
                "CHANNEL_RELAY_UNAVAILABLE",
                $"Relay for channel {request.ChannelId} is not configured or unavailable."));
        }

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
            StorageMode = BlobStorageMode.RelayHosted,
            RelayEndpointUri = uploadResult.Value!,
            BlurHash = request.BlurHash,
            InlineThumbnail = request.InlineThumbnail,
            Waveform = request.Waveform,
            Width = request.Width,
            Height = request.Height,
            DurationSeconds = request.DurationSeconds
        };

        return DomainResult<BlobUploadResult>.Success(
            new BlobUploadResult(relayedRef, BlobStorageMode.RelayHosted));
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
            // 2. Fetch from Relay
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

            await _localChunkStore.SaveBlobAsync(request.Reference.Id, downloadResult.Value!, ct).ConfigureAwait(false);
            ciphertextStream = await _localChunkStore.OpenReadAsync(request.Reference.Id, ct).ConfigureAwait(false);
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

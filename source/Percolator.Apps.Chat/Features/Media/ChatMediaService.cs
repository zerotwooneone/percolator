using Percolator.Domain.Channels.ValueObjects;
using Percolator.Domain.Common;
using Percolator.PluginSdk;

namespace Percolator.Apps.Chat.Features.Media;

public sealed class ChatMediaService
{
    public const long InlineThresholdBytes = 32 * 1024; // 32 KB
    public const long MaxAttachmentBytes = 50 * 1024 * 1024; // 50 MB

    private readonly IBlobStorageService? _blobStorage;

    public ChatMediaService(IBlobStorageService? blobStorage = null)
    {
        _blobStorage = blobStorage;
    }

    public bool IsInlineEligible(long byteSize) => byteSize >= 0 && byteSize <= InlineThresholdBytes;

    public string? SanitizeFileName(string? fileName, string contentType) =>
        SanitizeMediaFileName(fileName, contentType);

    public static string? SanitizeMediaFileName(string? fileName, string contentType)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return null;
        }

        // Section 2.6: Photo/video/audio filenames are stripped client-side before transmission
        if (!string.IsNullOrEmpty(contentType) &&
            (contentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase) ||
             contentType.StartsWith("video/", StringComparison.OrdinalIgnoreCase) ||
             contentType.StartsWith("audio/", StringComparison.OrdinalIgnoreCase)))
        {
            return null;
        }

        string normalized = fileName.Replace('\\', '/');
        string baseName = Path.GetFileName(normalized);
        return string.IsNullOrWhiteSpace(baseName) ? null : baseName;
    }

    public DomainResult<MediaAttachmentDto> PrepareInlineAttachment(
        BlobId blobId,
        string? originalFileName,
        string contentType,
        ReadOnlyMemory<byte> inlineBytes,
        int? width = null,
        int? height = null,
        byte[]? waveform = null)
    {
        if (inlineBytes.Length > MaxAttachmentBytes)
        {
            return DomainResult<MediaAttachmentDto>.Failure(new DomainError(
                "ATTACHMENT_TOO_LARGE", "Attachment exceeds maximum size limit of 50 MB."));
        }

        if (inlineBytes.Length > InlineThresholdBytes)
        {
            return DomainResult<MediaAttachmentDto>.Failure(new DomainError(
                "INLINE_THRESHOLD_EXCEEDED",
                $"Inline attachments cannot exceed {InlineThresholdBytes} bytes (32 KB). Use out-of-band blob storage instead."));
        }

        var sanitizedFileName = SanitizeMediaFileName(originalFileName, contentType);

        var attachment = new MediaAttachmentDto(
            blobId,
            sanitizedFileName,
            contentType,
            inlineBytes.Length,
            width,
            height,
            waveform,
            inlineBytes);

        return DomainResult<MediaAttachmentDto>.Success(attachment);
    }

    public async ValueTask<DomainResult<MediaAttachmentDto>> UploadOutOfBandAttachmentAsync(
        Stream plaintextStream,
        long byteSize,
        ChannelId channelId,
        string? originalFileName,
        string contentType,
        int? width = null,
        int? height = null,
        byte[]? waveform = null,
        TimeSpan? timeToLive = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(plaintextStream);

        if (byteSize <= 0)
        {
            return DomainResult<MediaAttachmentDto>.Failure(new DomainError(
                "INVALID_ATTACHMENT_SIZE", "Attachment size must be positive."));
        }

        if (byteSize > MaxAttachmentBytes)
        {
            return DomainResult<MediaAttachmentDto>.Failure(new DomainError(
                "ATTACHMENT_TOO_LARGE", "Attachment exceeds maximum size limit of 50 MB."));
        }

        if (_blobStorage is null)
        {
            return DomainResult<MediaAttachmentDto>.Failure(new DomainError(
                "BLOB_STORAGE_UNAVAILABLE", "Out-of-band blob storage service is not configured."));
        }

        var uploadRequest = new BlobUploadRequest(
            plaintextStream,
            contentType,
            byteSize,
            channelId,
            TimeToLive: timeToLive,
            Waveform: waveform,
            Width: width,
            Height: height);

        var uploadResult = await _blobStorage.UploadBlobAsync(uploadRequest, ct: ct).ConfigureAwait(false);
        if (uploadResult.IsFailure)
        {
            return DomainResult<MediaAttachmentDto>.Failure(uploadResult.Error);
        }

        var sanitizedFileName = SanitizeMediaFileName(originalFileName, contentType);
        var attachment = new MediaAttachmentDto(
            uploadResult.Value.Reference.Id,
            sanitizedFileName,
            contentType,
            byteSize,
            width,
            height,
            waveform,
            InlineBytes: null);

        return DomainResult<MediaAttachmentDto>.Success(attachment);
    }

    public async ValueTask PurgeLocalAttachmentBlobsAsync(
        IEnumerable<MediaAttachmentDto>? attachments,
        CancellationToken ct = default)
    {
        if (_blobStorage is null || attachments is null)
        {
            return;
        }

        foreach (var attachment in attachments)
        {
            if (!string.IsNullOrWhiteSpace(attachment.BlobId.HexDigest))
            {
                await _blobStorage.DeleteLocalCacheAsync(attachment.BlobId, ct).ConfigureAwait(false);
            }
        }
    }
}

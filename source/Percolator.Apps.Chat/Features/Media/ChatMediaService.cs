using Percolator.Domain.Common;
using Percolator.PluginSdk;

namespace Percolator.Apps.Chat.Features.Media;

public sealed class ChatMediaService
{
    public const long InlineThresholdBytes = 32 * 1024; // 32 KB
    public const long MaxAttachmentBytes = 50 * 1024 * 1024; // 50 MB

    public bool IsInlineEligible(long byteSize) => byteSize <= InlineThresholdBytes;

    public string? SanitizeFileName(string? fileName, string contentType)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return null;
        }

        // Section 2.6: Photo/video/audio filenames are stripped client-side before transmission
        if (contentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase) ||
            contentType.StartsWith("video/", StringComparison.OrdinalIgnoreCase) ||
            contentType.StartsWith("audio/", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return Path.GetFileName(fileName);
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
            return DomainResult<MediaAttachmentDto>.Failure(new DomainError("ATTACHMENT_TOO_LARGE", "Attachment exceeds maximum size limit of 50 MB."));
        }

        var sanitizedFileName = SanitizeFileName(originalFileName, contentType);

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
}

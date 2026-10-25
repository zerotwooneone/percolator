using Percolator.PluginSdk;

namespace Percolator.Apps.Chat.Features.Media;

public sealed record MediaAttachmentDto(
    BlobId BlobId,
    string? FileName,
    string ContentType,
    long ByteSize,
    int? Width,
    int? Height,
    byte[]? Waveform,
    ReadOnlyMemory<byte>? InlineBytes,
    BlobReference? OutOfBandReference = null)
{
    public static MediaAttachmentDto FromOutOfBand(
        BlobReference reference,
        string? fileName = null)
    {
        ArgumentNullException.ThrowIfNull(reference);
        return new MediaAttachmentDto(
            reference.Id,
            fileName,
            reference.MimeType,
            reference.PlaintextSizeBytes,
            reference.Width,
            reference.Height,
            reference.Waveform,
            InlineBytes: null,
            OutOfBandReference: reference);
    }
}

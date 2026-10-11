using Percolator.PluginSdk;

namespace Percolator.Apps.Chat.Models;

public sealed record MediaAttachmentDto(
    BlobId BlobId,
    string? FileName,
    string ContentType,
    long ByteSize,
    int? Width,
    int? Height,
    byte[]? Waveform,
    ReadOnlyMemory<byte>? InlineBytes);

using Percolator.Domain.Channels.ValueObjects;

namespace Percolator.PluginSdk;

public sealed record BlobUploadRequest(
    Stream ContentStream,
    string MimeType,
    long ContentLength,
    ChannelId ChannelId,
    TimeSpan? TimeToLive = null,
    string? BlurHash = null,
    byte[]? InlineThumbnail = null,
    byte[]? Waveform = null,
    int? Width = null,
    int? Height = null,
    int? DurationSeconds = null);

public sealed record BlobUploadResult(
    BlobReference Reference,
    BlobStorageMode StorageMode);

public sealed record BlobDownloadRequest(
    BlobReference Reference,
    CancellationToken CancellationToken = default);

public sealed record BlobDownloadResult(
    Stream ContentStream,
    string MimeType,
    long ContentLength);

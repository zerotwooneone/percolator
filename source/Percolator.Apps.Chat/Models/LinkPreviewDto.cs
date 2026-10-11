using Percolator.PluginSdk;

namespace Percolator.Apps.Chat.Models;

public sealed record LinkPreviewDto(
    string Title,
    string Description,
    string CanonicalUrl,
    BlobReference? ThumbnailBlob);

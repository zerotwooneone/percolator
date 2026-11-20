using Percolator.Apps.FileTransfer.Domain.Common;
using Percolator.Apps.FileTransfer.Domain.ManifestManagement;

namespace Percolator.Apps.FileTransfer.Ports;

public sealed record ManifestFileReadModel(
    FileIndex Index,
    SanitizedRelativePath RelativePath,
    FileSizeBytes FileSize,
    BundleByteOffset StartOffset,
    BundleByteOffset EndOffset,
    FileLastModifiedUtc LastModifiedUtc,
    bool IsSelected,
    bool IsCompleted);

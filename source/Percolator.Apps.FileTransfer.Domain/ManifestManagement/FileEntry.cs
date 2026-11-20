using Percolator.Apps.FileTransfer.Domain.Common;
using Percolator.Domain.Common;

namespace Percolator.Apps.FileTransfer.Domain.ManifestManagement;

public sealed class FileEntry : IEntity<FileIndex>
{
    public FileIndex Id => Index;
    public FileIndex Index { get; }
    public SanitizedRelativePath RelativePath { get; }
    public FileSizeBytes FileSize { get; }
    public BundleByteOffset StartOffset { get; }
    public BundleByteOffset EndOffset => StartOffset.Add(FileSize);
    public FileLastModifiedUtc LastModifiedUtc { get; }

    public FileEntry(
        FileIndex index,
        SanitizedRelativePath relativePath,
        FileSizeBytes fileSize,
        BundleByteOffset startOffset,
        FileLastModifiedUtc lastModifiedUtc)
    {
        Index = index;
        RelativePath = relativePath;
        FileSize = fileSize;
        StartOffset = startOffset;
        LastModifiedUtc = lastModifiedUtc;
    }
}

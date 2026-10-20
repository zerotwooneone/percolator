using Percolator.Apps.FileTransfer.Domain.Common;

namespace Percolator.Apps.FileTransfer.Domain.ManifestManagement;

public sealed record FileChunkSlice(
    FileIndex FileIndex,
    SanitizedRelativePath RelativePath,
    FileByteOffset OffsetInFile,
    TransferByteLength SliceLength,
    int BufferOffset);

using System.Text.Json;
using Percolator.Apps.FileTransfer.Domain.Exceptions;

namespace Percolator.Apps.FileTransfer.Domain.ManifestManagement;

public static class TransferManifestSerializer
{
    public static byte[] Serialize(TransferManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        var dto = new
        {
            manifestId = manifest.ManifestId.Value.ToString(),
            rootName = manifest.RootName.Value,
            chunkSize = manifest.ChunkSize.Bytes,
            merkleRoot = Convert.ToBase64String(manifest.MerkleTree.Root.Span),
            leaves = manifest.MerkleTree.Leaves.Select(l => Convert.ToBase64String(l.Span)).ToArray(),
            files = manifest.Files.Select(f => new
            {
                index = f.Index.Value,
                path = f.RelativePath.Value,
                size = f.FileSize.Bytes,
                startOffset = f.StartOffset.Offset,
                lastModified = f.LastModifiedUtc.UnixSeconds
            }).ToArray()
        };

        return JsonSerializer.SerializeToUtf8Bytes(dto);
    }
}

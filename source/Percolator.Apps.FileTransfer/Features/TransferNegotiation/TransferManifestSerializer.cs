using System.Text.Json;
using Percolator.Apps.FileTransfer.Domain.ManifestManagement;

namespace Percolator.Apps.FileTransfer.Features.TransferNegotiation;

public static class TransferManifestSerializer
{
    public static byte[] Serialize(TransferManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        using MemoryStream ms = new();
        using (Utf8JsonWriter writer = new(ms))
        {
            writer.WriteStartObject();
            writer.WriteString("manifestId", manifest.ManifestId.Value);
            writer.WriteString("rootName", manifest.RootName.Value);
            writer.WriteNumber("totalSize", manifest.TotalSize.Bytes);
            writer.WriteNumber("chunkSize", manifest.ChunkSize.Bytes);
            writer.WriteString("merkleRoot", Convert.ToBase64String(manifest.MerkleTree.Root.Span));

            writer.WriteStartArray("leaves");
            for (int i = 0; i < manifest.MerkleTree.Leaves.Count; i++)
            {
                writer.WriteStringValue(Convert.ToBase64String(manifest.MerkleTree.Leaves[i].Span));
            }
            writer.WriteEndArray();

            writer.WriteStartArray("files");
            foreach (FileEntry file in manifest.Files)
            {
                writer.WriteStartObject();
                writer.WriteNumber("index", file.Index.Value);
                writer.WriteString("path", file.RelativePath.Value);
                writer.WriteNumber("size", file.FileSize.Bytes);
                writer.WriteNumber("startOffset", file.StartOffset.Offset);
                writer.WriteNumber("lastModified", file.LastModifiedUtc.UnixSeconds);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();

            writer.WriteEndObject();
        }

        return ms.ToArray();
    }
}

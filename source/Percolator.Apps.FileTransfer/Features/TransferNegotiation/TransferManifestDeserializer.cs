using System.Text.Json;
using Percolator.Apps.FileTransfer.Domain.Common;
using Percolator.Apps.FileTransfer.Domain.Exceptions;
using Percolator.Apps.FileTransfer.Domain.ManifestManagement;
using Percolator.Apps.FileTransfer.Domain.TransferNegotiation;

namespace Percolator.Apps.FileTransfer.Features.TransferNegotiation;

public static class TransferManifestDeserializer
{
    public static TransferManifest Deserialize(ReadOnlySpan<byte> span, TransferSessionId fallbackId)
    {
        if (span.IsEmpty)
            throw new DomainValidationException("Manifest data cannot be empty.");

        try
        {
            using JsonDocument doc = JsonDocument.Parse(span.ToArray());
            JsonElement root = doc.RootElement;

            TransferSessionId manifestId = root.TryGetProperty("manifestId", out JsonElement idEl) && Guid.TryParse(idEl.GetString(), out Guid g)
                ? TransferSessionId.FromGuid(g)
                : fallbackId;

            string rootNameStr = root.GetProperty("rootName").GetString() ?? "bundle";
            RootBundleName rootName = RootBundleName.Parse(rootNameStr);

            int chunkSizeVal = root.GetProperty("chunkSize").GetInt32();
            ChunkSizeBytes chunkSize = ChunkSizeBytes.FromBytes(chunkSizeVal);

            string merkleRootB64 = root.GetProperty("merkleRoot").GetString()!;
            MerkleRoot merkleRoot = MerkleRoot.FromBytes(Convert.FromBase64String(merkleRootB64));

            List<ChunkHash> leaves = [];
            if (root.TryGetProperty("leaves", out JsonElement leavesEl) && leavesEl.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement leafEl in leavesEl.EnumerateArray())
                {
                    string leafB64 = leafEl.GetString()!;
                    leaves.Add(ChunkHash.FromBytes(Convert.FromBase64String(leafB64)));
                }
            }

            MerkleTreeIndex merkleTree = MerkleTreeIndex.Create(merkleRoot, leaves);

            List<FileEntry> files = [];
            if (root.TryGetProperty("files", out JsonElement filesEl) && filesEl.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement fileEl in filesEl.EnumerateArray())
                {
                    int index = fileEl.GetProperty("index").GetInt32();
                    string path = fileEl.GetProperty("path").GetString()!;
                    long size = fileEl.GetProperty("size").GetInt64();
                    long startOffset = fileEl.GetProperty("startOffset").GetInt64();
                    long lastModified = fileEl.GetProperty("lastModified").GetInt64();

                    files.Add(new FileEntry(
                        FileIndex.FromInt(index),
                        SanitizedRelativePath.Parse(path),
                        FileSizeBytes.FromBytes(size),
                        BundleByteOffset.FromOffset(startOffset),
                        FileLastModifiedUtc.FromUnixSeconds(lastModified)));
                }
            }

            return TransferManifest.Create(manifestId, rootName, chunkSize, merkleTree, files);
        }
        catch (Exception ex) when (ex is not DomainValidationException)
        {
            throw new DomainValidationException("Failed to deserialize transfer manifest.", ex);
        }
    }
}

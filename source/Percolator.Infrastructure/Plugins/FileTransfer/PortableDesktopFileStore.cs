namespace Percolator.Infrastructure.Plugins.FileTransfer;

using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Percolator.Apps.FileTransfer.Domain.Common;
using Percolator.Apps.FileTransfer.Domain.Exceptions;
using Percolator.Apps.FileTransfer.Domain.ManifestManagement;
using Percolator.Apps.FileTransfer.Domain.TransferNegotiation;
using Percolator.Apps.FileTransfer.Features.Storage;
using Percolator.Apps.FileTransfer.Features.TransferNegotiation;
using Percolator.Apps.FileTransfer.Ports;

public sealed class PortableDesktopFileStore : IDesktopFileStorage
{
    private readonly string _boundaryPartialsBaseDirectory;

    public PortableDesktopFileStore(string? partialsBaseDirectory = null)
    {
        _boundaryPartialsBaseDirectory = !string.IsNullOrWhiteSpace(partialsBaseDirectory)
            ? partialsBaseDirectory
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Percolator", "Partials");
    }

    public ValueTask PreAllocateFilesAsync(
        RootDirectoryPath rootPath,
        TransferManifest manifest,
        SelectedFileIndices selectedIndices,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(rootPath);
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(selectedIndices);

        for (int i = 0; i < manifest.Files.Count; i++)
        {
            FileIndex fileIndex = FileIndex.FromInt(i);
            if (!selectedIndices.Contains(fileIndex))
            {
                continue;
            }

            FileEntry entry = manifest.Files[i];
            ResolvedFilePath targetFile = ResolveAndValidateContainedPath(rootPath, entry.RelativePath);

            if (entry.FileSize.Bytes == 0)
            {
                EnsureParentDirectoryExists(targetFile.AbsolutePath);
                using FileStream emptyFs = new(targetFile.AbsolutePath, FileMode.OpenOrCreate, FileAccess.Write, FileShare.ReadWrite);
            }
            else
            {
                string partPath = GetPartFilePath(targetFile.AbsolutePath);
                EnsureParentDirectoryExists(partPath);

                using FileStream partFs = new(partPath, FileMode.OpenOrCreate, FileAccess.Write, FileShare.ReadWrite);
                if (partFs.Length != entry.FileSize.Bytes)
                {
                    partFs.SetLength(entry.FileSize.Bytes);
                }
            }
        }

        return ValueTask.CompletedTask;
    }

    public async ValueTask WriteChunkAsync(
        ResolvedFilePath targetPath,
        FileByteOffset fileOffset,
        PlaintextChunkData data,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(targetPath);
        ArgumentNullException.ThrowIfNull(data);

        string partPath = GetPartFilePath(targetPath.AbsolutePath);
        string effectivePath = File.Exists(partPath) ? partPath : targetPath.AbsolutePath;

        EnsureParentDirectoryExists(effectivePath);

        await using FileStream fs = new(
            effectivePath,
            FileMode.OpenOrCreate,
            FileAccess.Write,
            FileShare.ReadWrite,
            bufferSize: 4096,
            useAsync: true);

        fs.Seek(fileOffset.Offset, SeekOrigin.Begin);
        await fs.WriteAsync(data.Memory, ct).ConfigureAwait(false);
        await fs.FlushAsync(ct).ConfigureAwait(false);
    }

    public async ValueTask<PlaintextChunkData> ReadChunkAsync(
        ResolvedFilePath sourcePath,
        FileByteOffset fileOffset,
        TransferByteLength length,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(sourcePath);

        string partPath = GetPartFilePath(sourcePath.AbsolutePath);
        string effectivePath = File.Exists(sourcePath.AbsolutePath) ? sourcePath.AbsolutePath : partPath;

        if (!File.Exists(effectivePath))
        {
            throw new FileNotFoundException($"Cannot read chunk from non-existent file: {effectivePath}");
        }

        byte[] buffer = new byte[length.Bytes];
        await using (FileStream fs = new(
            effectivePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite,
            bufferSize: 4096,
            useAsync: true))
        {
            fs.Seek(fileOffset.Offset, SeekOrigin.Begin);
            int totalRead = 0;
            while (totalRead < length.Bytes)
            {
                int read = await fs.ReadAsync(buffer.AsMemory(totalRead, length.Bytes - totalRead), ct).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }
                totalRead += read;
            }

            if (totalRead < length.Bytes)
            {
                Array.Resize(ref buffer, totalRead);
            }
        }

        return PlaintextChunkData.FromBytes(buffer);
    }

    public async ValueTask WriteBoundarySliceAsync(
        TransferSessionId sessionId,
        ChunkIndex chunkIndex,
        FileIndex fileIndex,
        PlaintextChunkData data,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(data);

        string sessionFolder = Path.Combine(_boundaryPartialsBaseDirectory, sessionId.Value.ToString("N"));
        Directory.CreateDirectory(sessionFolder);

        string slicePath = Path.Combine(sessionFolder, $"{chunkIndex.Value}_{fileIndex.Value}.slice");

        await using FileStream fs = new(
            slicePath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.Read,
            bufferSize: 4096,
            useAsync: true);

        await fs.WriteAsync(data.Memory, ct).ConfigureAwait(false);
        await fs.FlushAsync(ct).ConfigureAwait(false);
    }

    public async ValueTask<PlaintextChunkData?> ReadBoundarySliceAsync(
        TransferSessionId sessionId,
        ChunkIndex chunkIndex,
        FileIndex fileIndex,
        TransferByteLength length,
        CancellationToken ct = default)
    {
        string slicePath = Path.Combine(_boundaryPartialsBaseDirectory, sessionId.Value.ToString("N"), $"{chunkIndex.Value}_{fileIndex.Value}.slice");
        if (!File.Exists(slicePath))
        {
            return null;
        }

        byte[] buffer = new byte[length.Bytes];
        await using (FileStream fs = new(
            slicePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite,
            bufferSize: 4096,
            useAsync: true))
        {
            int totalRead = 0;
            while (totalRead < length.Bytes)
            {
                int read = await fs.ReadAsync(buffer.AsMemory(totalRead, length.Bytes - totalRead), ct).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }
                totalRead += read;
            }

            if (totalRead < length.Bytes)
            {
                Array.Resize(ref buffer, totalRead);
            }
        }

        return PlaintextChunkData.FromBytes(buffer);
    }

    public ValueTask DeleteBoundaryPartsAsync(TransferSessionId sessionId, CancellationToken ct = default)
    {
        string sessionFolder = Path.Combine(_boundaryPartialsBaseDirectory, sessionId.Value.ToString("N"));
        if (Directory.Exists(sessionFolder))
        {
            try
            {
                Directory.Delete(sessionFolder, recursive: true);
            }
            catch (IOException)
            {
            }
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask FinalizeFileAsync(
        ResolvedFilePath targetPath,
        FileLastModifiedUtc lastModifiedUtc,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(targetPath);

        string partPath = GetPartFilePath(targetPath.AbsolutePath);
        if (File.Exists(partPath))
        {
            if (File.Exists(targetPath.AbsolutePath))
            {
                File.Delete(targetPath.AbsolutePath);
            }

            File.Move(partPath, targetPath.AbsolutePath);
        }

        if (File.Exists(targetPath.AbsolutePath))
        {
            DateTime utcDate = lastModifiedUtc.UtcDateTime.UtcDateTime;
            if (utcDate > DateTime.MinValue && utcDate < DateTime.MaxValue)
            {
                File.SetLastWriteTimeUtc(targetPath.AbsolutePath, utcDate);
            }
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask<DiskSpacePreflightResult> CheckDiskSpaceAsync(
        RootDirectoryPath rootPath,
        TotalBundleBytes requiredBytes,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(rootPath);

        try
        {
            string root = Path.GetPathRoot(Path.GetFullPath(rootPath.Value)) ?? rootPath.Value;
            DriveInfo drive = new(root);
            long available = drive.AvailableFreeSpace;

            return ValueTask.FromResult(available >= requiredBytes.Bytes
                ? DiskSpacePreflightResult.Sufficient(requiredBytes, TotalBundleBytes.FromBytes(available))
                : DiskSpacePreflightResult.Insufficient(requiredBytes, TotalBundleBytes.FromBytes(available)));
        }
        catch (Exception)
        {
            return ValueTask.FromResult(DiskSpacePreflightResult.Sufficient(requiredBytes, requiredBytes));
        }
    }

    public async ValueTask<Bitfield> ScanAndComputeBitfieldAsync(
        RootDirectoryPath rootPath,
        TransferManifest manifest,
        SelectedFileIndices? selectedIndices = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(rootPath);
        ArgumentNullException.ThrowIfNull(manifest);

        Bitfield bitfield = Bitfield.Empty(manifest.TotalChunks);
        byte[] rentedBuffer = ArrayPool<byte>.Shared.Rent(manifest.ChunkSize.Bytes);

        try
        {
            for (int c = 0; c < manifest.TotalChunks.Count; c++)
            {
                ct.ThrowIfCancellationRequested();
                ChunkIndex chunkIndex = ChunkIndex.FromInt(c);
                int expectedBytes = (int)Math.Min(manifest.ChunkSize.Bytes, manifest.TotalSize.Bytes - ((long)c * manifest.ChunkSize.Bytes));
                ChunkByteLength expectedSize = ChunkByteLength.FromBytes(expectedBytes);

                IReadOnlyList<FileChunkSlice> slices = ChunkBoundaryCalculator.CalculateSlices(
                    manifest,
                    chunkIndex,
                    expectedSize);

                bool allSlicesPresent = true;
                foreach (FileChunkSlice slice in slices)
                {
                    if (selectedIndices != null && !selectedIndices.Contains(slice.FileIndex))
                    {
                        string slicePath = Path.Combine(_boundaryPartialsBaseDirectory, manifest.ManifestId.Value.ToString("N"), $"{chunkIndex.Value}_{slice.FileIndex.Value}.slice");
                        if (!File.Exists(slicePath) || new FileInfo(slicePath).Length < slice.SliceLength.Bytes)
                        {
                            allSlicesPresent = false;
                            break;
                        }
                    }
                    else
                    {
                        FileEntry entry = manifest.Files[slice.FileIndex.Value];
                        ResolvedFilePath resolvedPath = ResolveAndValidateContainedPath(rootPath, entry.RelativePath);
                        string partPath = GetPartFilePath(resolvedPath.AbsolutePath);

                        string effective = File.Exists(resolvedPath.AbsolutePath) ? resolvedPath.AbsolutePath : partPath;
                        if (!File.Exists(effective) || new FileInfo(effective).Length < (slice.OffsetInFile.Offset + slice.SliceLength.Bytes))
                        {
                            allSlicesPresent = false;
                            break;
                        }
                    }
                }

                if (!allSlicesPresent)
                {
                    continue;
                }

                Span<byte> activeSpan = rentedBuffer.AsSpan(0, expectedBytes);
                activeSpan.Clear();

                foreach (FileChunkSlice slice in slices)
                {
                    PlaintextChunkData sliceData;
                    if (selectedIndices != null && !selectedIndices.Contains(slice.FileIndex))
                    {
                        PlaintextChunkData? boundarySlice = await ReadBoundarySliceAsync(
                            manifest.ManifestId,
                            chunkIndex,
                            slice.FileIndex,
                            slice.SliceLength,
                            ct).ConfigureAwait(false);

                        if (boundarySlice == null)
                        {
                            allSlicesPresent = false;
                            break;
                        }

                        sliceData = boundarySlice;
                    }
                    else
                    {
                        FileEntry entry = manifest.Files[slice.FileIndex.Value];
                        ResolvedFilePath resolvedPath = ResolveAndValidateContainedPath(rootPath, entry.RelativePath);
                        sliceData = await ReadChunkAsync(resolvedPath, slice.OffsetInFile, slice.SliceLength, ct).ConfigureAwait(false);
                    }

                    sliceData.Span.CopyTo(rentedBuffer.AsSpan(slice.BufferOffset, slice.SliceLength.Bytes));
                }

                if (!allSlicesPresent)
                {
                    continue;
                }

                ChunkHash computedHash = MerkleTreeBuilder.HashLeaf(rentedBuffer.AsSpan(0, expectedBytes));
                if (computedHash == manifest.MerkleTree.Leaves[c])
                {
                    bitfield = bitfield.WithChunk(chunkIndex);
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rentedBuffer);
        }

        return bitfield;
    }

    public static ResolvedFilePath ResolveAndValidateContainedPath(RootDirectoryPath rootPath, SanitizedRelativePath relativePath)
    {
        ArgumentNullException.ThrowIfNull(rootPath);
        ArgumentNullException.ThrowIfNull(relativePath);

        string rawRelative = relativePath.Value;
        if (rawRelative.Contains('\0') || rawRelative.Contains("..", StringComparison.Ordinal))
        {
            throw new DomainValidationException($"Relative path '{rawRelative}' contains illegal characters or path traversal sequences.");
        }

        if (Path.IsPathRooted(rawRelative) || rawRelative.StartsWith('/') || rawRelative.StartsWith('\\'))
        {
            throw new DomainValidationException($"Relative path '{rawRelative}' cannot be absolute or start with a directory separator.");
        }

        string canonicalRoot = Path.GetFullPath(rootPath.Value).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string combinedPath = Path.GetFullPath(Path.Combine(canonicalRoot, rawRelative));

        if (!combinedPath.StartsWith(canonicalRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            && !string.Equals(combinedPath, canonicalRoot, StringComparison.Ordinal))
        {
            throw new DomainValidationException($"Resolved path '{combinedPath}' escapes target root directory '{canonicalRoot}'.");
        }

        return ResolvedFilePath.Resolve(rootPath, relativePath);
    }

    private static string GetPartFilePath(string absolutePath) => $"{absolutePath}.percolator-part";

    private static void EnsureParentDirectoryExists(string filePath)
    {
        string? parent = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(parent) && !Directory.Exists(parent))
        {
            Directory.CreateDirectory(parent);
        }
    }
}

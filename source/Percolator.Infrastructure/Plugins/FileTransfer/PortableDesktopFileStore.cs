namespace Percolator.Infrastructure.Plugins.FileTransfer;

using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;
using Percolator.Apps.FileTransfer.Domain.Common;
using Percolator.Apps.FileTransfer.Domain.Exceptions;
using Percolator.Apps.FileTransfer.Domain.ManifestManagement;
using Percolator.Apps.FileTransfer.Domain.TransferNegotiation;
using Percolator.Apps.FileTransfer.Features.Storage;
using Percolator.Apps.FileTransfer.Features.TransferNegotiation;
using Percolator.Apps.FileTransfer.Ports;

public sealed class PortableDesktopFileStore : IDesktopFileStorage, IDisposable
{
    private readonly string _boundaryPartialsBaseDirectory;
    private readonly int _maxOpenHandles;
    private readonly object _handleCacheLock = new();
    private readonly Dictionary<HandleKey, HandleEntry> _handleCache = new();
    private readonly LinkedList<HandleKey> _lruList = new();
    private readonly ConcurrentDictionary<string, byte> _createdDirectories = new(StringComparer.OrdinalIgnoreCase);

    public PortableDesktopFileStore(string? partialsBaseDirectory = null, int maxOpenHandles = 256)
    {
        _boundaryPartialsBaseDirectory = !string.IsNullOrWhiteSpace(partialsBaseDirectory)
            ? partialsBaseDirectory
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Percolator", "Partials");
        _maxOpenHandles = Math.Max(1, maxOpenHandles);
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
                using FileStream emptyFs = new(targetFile.AbsolutePath, FileMode.OpenOrCreate, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
            }
            else if (entry.FileSize.Bytes >= 1024 * 1024)
            {
                string partPath = GetPartFilePath(targetFile.AbsolutePath);
                EnsureParentDirectoryExists(partPath);

                using FileStream partFs = new(partPath, FileMode.OpenOrCreate, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
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
        string effectivePath = File.Exists(targetPath.AbsolutePath) ? targetPath.AbsolutePath : partPath;

        EnsureParentDirectoryExists(effectivePath);

        HandleEntry entry = AcquireHandle(effectivePath, FileMode.OpenOrCreate, FileAccess.Write);
        try
        {
            await RandomAccess.WriteAsync(entry.Handle, data.Memory, fileOffset.Offset, ct).ConfigureAwait(false);
        }
        finally
        {
            ReleaseHandle(entry);
        }
    }

    public async ValueTask<int> ReadChunkIntoAsync(
        ResolvedFilePath sourcePath,
        FileByteOffset fileOffset,
        Memory<byte> destination,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(sourcePath);

        string partPath = GetPartFilePath(sourcePath.AbsolutePath);
        string effectivePath = File.Exists(sourcePath.AbsolutePath) ? sourcePath.AbsolutePath : partPath;

        if (!File.Exists(effectivePath))
        {
            throw new FileNotFoundException($"Cannot read chunk from non-existent file: {effectivePath}");
        }

        HandleEntry entry = AcquireHandle(effectivePath, FileMode.Open, FileAccess.Read);
        try
        {
            int totalRead = 0;
            int needed = destination.Length;
            while (totalRead < needed)
            {
                int read = await RandomAccess.ReadAsync(
                    entry.Handle,
                    destination.Slice(totalRead, needed - totalRead),
                    fileOffset.Offset + totalRead,
                    ct).ConfigureAwait(false);

                if (read == 0)
                {
                    break;
                }

                totalRead += read;
            }

            return totalRead;
        }
        finally
        {
            ReleaseHandle(entry);
        }
    }

    public async ValueTask<PlaintextChunkData> ReadChunkAsync(
        ResolvedFilePath sourcePath,
        FileByteOffset fileOffset,
        TransferByteLength length,
        CancellationToken ct = default)
    {
        byte[] buffer = new byte[length.Bytes];
        int totalRead = await ReadChunkIntoAsync(sourcePath, fileOffset, buffer, ct).ConfigureAwait(false);
        if (totalRead < length.Bytes)
        {
            Array.Resize(ref buffer, totalRead);
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

        EvictPath(targetPath.AbsolutePath);

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

    public ValueTask<bool> HasAnyExistingTransferFilesAsync(
        RootDirectoryPath rootPath,
        TransferManifest manifest,
        SelectedFileIndices selectedIndices,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(rootPath);
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(selectedIndices);

        if (!Directory.Exists(rootPath.Value))
        {
            return ValueTask.FromResult(false);
        }

        try
        {
            using IEnumerator<string> enumerator = Directory.EnumerateFileSystemEntries(rootPath.Value).GetEnumerator();
            bool hasEntries = enumerator.MoveNext();
            return ValueTask.FromResult(hasEntries);
        }
        catch (Exception)
        {
            return ValueTask.FromResult(true);
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

        Bitfield.BitfieldBuilder bitfieldBuilder = new(manifest.TotalChunks);
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

                        boundarySlice.Span.CopyTo(rentedBuffer.AsSpan(slice.BufferOffset, slice.SliceLength.Bytes));
                    }
                    else
                    {
                        FileEntry entry = manifest.Files[slice.FileIndex.Value];
                        ResolvedFilePath resolvedPath = ResolveAndValidateContainedPath(rootPath, entry.RelativePath);
                        await ReadChunkIntoAsync(
                            resolvedPath,
                            slice.OffsetInFile,
                            rentedBuffer.AsMemory(slice.BufferOffset, slice.SliceLength.Bytes),
                            ct).ConfigureAwait(false);
                    }
                }

                if (!allSlicesPresent)
                {
                    continue;
                }

                if (manifest.MerkleTree.VerifyLeaf(chunkIndex, rentedBuffer.AsSpan(0, expectedBytes)))
                {
                    bitfieldBuilder.Set(chunkIndex);
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rentedBuffer);
        }

        return bitfieldBuilder.Build();
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

    private void EnsureParentDirectoryExists(string filePath)
    {
        string? parent = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(parent))
        {
            string normalizedParent = Path.GetFullPath(parent);
            _createdDirectories.GetOrAdd(normalizedParent, p =>
            {
                if (!Directory.Exists(p))
                {
                    Directory.CreateDirectory(p);
                }
                return 0;
            });
        }
    }

    private HandleEntry AcquireHandle(string path, FileMode mode, FileAccess access)
    {
        string normalized = Path.GetFullPath(path);
        var key = new HandleKey(normalized, access);

        lock (_handleCacheLock)
        {
            if (_handleCache.TryGetValue(key, out HandleEntry? existing))
            {
                if (!existing.Handle.IsClosed &&
                    !existing.Handle.IsInvalid &&
                    !existing.MarkedForEviction)
                {
                    existing.RefCount++;
                    if (existing.LruNode != null)
                    {
                        _lruList.Remove(existing.LruNode);
                        _lruList.AddLast(existing.LruNode);
                    }
                    return existing;
                }

                _handleCache.Remove(key);
                if (existing.LruNode != null)
                {
                    _lruList.Remove(existing.LruNode);
                }
            }

            EvictLruHandlesUnderLock();

            FileShare share = FileShare.ReadWrite | FileShare.Delete;
            FileOptions options = FileOptions.Asynchronous | FileOptions.RandomAccess;
            SafeFileHandle handle = File.OpenHandle(normalized, mode, access, share, options);

            var node = new LinkedListNode<HandleKey>(key);
            var entry = new HandleEntry(handle) { LruNode = node };
            _handleCache[key] = entry;
            _lruList.AddLast(node);
            return entry;
        }
    }

    private void ReleaseHandle(HandleEntry entry)
    {
        SafeFileHandle? toDispose = null;
        lock (_handleCacheLock)
        {
            entry.RefCount--;
            if (entry.MarkedForEviction && entry.RefCount == 0)
            {
                toDispose = entry.Handle;
            }
        }
        toDispose?.Dispose();
    }

    private void EvictLruHandlesUnderLock()
    {
        while (_handleCache.Count >= _maxOpenHandles && _lruList.First != null)
        {
            LinkedListNode<HandleKey>? curr = _lruList.First;
            bool evictedAny = false;
            while (curr != null)
            {
                HandleKey key = curr.Value;
                if (_handleCache.TryGetValue(key, out HandleEntry? e) && e.RefCount == 0)
                {
                    _handleCache.Remove(key);
                    _lruList.Remove(curr);
                    e.MarkedForEviction = true;
                    e.Handle.Dispose();
                    evictedAny = true;
                    break;
                }
                curr = curr.Next;
            }

            if (!evictedAny)
            {
                break;
            }
        }
    }

    private void EvictPath(string path)
    {
        string normTarget = Path.GetFullPath(path);
        string normPart = Path.GetFullPath(GetPartFilePath(path));

        lock (_handleCacheLock)
        {
            EvictKeyUnderLock(new HandleKey(normTarget, FileAccess.Read));
            EvictKeyUnderLock(new HandleKey(normTarget, FileAccess.Write));
            EvictKeyUnderLock(new HandleKey(normPart, FileAccess.Read));
            EvictKeyUnderLock(new HandleKey(normPart, FileAccess.Write));
        }
    }

    private void EvictKeyUnderLock(HandleKey key)
    {
        if (_handleCache.Remove(key, out HandleEntry? entry))
        {
            if (entry.LruNode != null)
            {
                _lruList.Remove(entry.LruNode);
            }
            entry.MarkedForEviction = true;
            if (entry.RefCount == 0)
            {
                try
                {
                    entry.Handle.Dispose();
                }
                catch
                {
                }
            }
        }
    }

    public void Dispose()
    {
        lock (_handleCacheLock)
        {
            foreach (HandleEntry entry in _handleCache.Values)
            {
                try
                {
                    entry.Handle.Dispose();
                }
                catch
                {
                }
            }
            _handleCache.Clear();
            _lruList.Clear();
        }
    }

    private readonly record struct HandleKey(string NormalizedPath, FileAccess Access);

    private sealed class HandleEntry
    {
        public SafeFileHandle Handle { get; }
        public int RefCount;
        public bool MarkedForEviction { get; set; }
        public LinkedListNode<HandleKey>? LruNode { get; set; }

        public HandleEntry(SafeFileHandle handle)
        {
            Handle = handle;
            RefCount = 1;
        }
    }
}

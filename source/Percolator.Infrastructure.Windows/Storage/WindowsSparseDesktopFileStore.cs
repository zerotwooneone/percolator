namespace Percolator.Infrastructure.Windows.Storage;

using System;
using System.Buffers;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;
using Percolator.Apps.FileTransfer.Domain.Common;
using Percolator.Apps.FileTransfer.Domain.Exceptions;
using Percolator.Apps.FileTransfer.Domain.ManifestManagement;
using Percolator.Apps.FileTransfer.Domain.TransferNegotiation;
using Percolator.Apps.FileTransfer.Features.TransferNegotiation;
using Percolator.Apps.FileTransfer.Ports;

[SupportedOSPlatform("windows")]
public sealed partial class WindowsSparseDesktopFileStore : IDesktopFileStorage
{
    private const uint FSCTL_SET_SPARSE = 0x000900C4;
    private const int ERROR_INVALID_FUNCTION = 1;
    private const int ERROR_NOT_SUPPORTED = 50;

    private static readonly HashSet<string> ReservedDeviceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "COM¹", "COM²", "COM³",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
        "LPT¹", "LPT²", "LPT³"
    };

    private readonly string _boundaryPartialsBaseDirectory;

    public WindowsSparseDesktopFileStore(string? partialsBaseDirectory = null)
    {
        _boundaryPartialsBaseDirectory = !string.IsNullOrWhiteSpace(partialsBaseDirectory)
            ? partialsBaseDirectory
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Percolator", "Partials");
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeviceIoControl(
        SafeFileHandle hDevice,
        uint dwIoControlCode,
        nint lpInBuffer,
        uint nInBufferSize,
        nint lpOutBuffer,
        uint nOutBufferSize,
        out uint lpBytesReturned,
        nint lpOverlapped);

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
            ct.ThrowIfCancellationRequested();
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
                using SafeFileHandle emptyHandle = File.OpenHandle(
                    targetFile.AbsolutePath,
                    FileMode.OpenOrCreate,
                    FileAccess.Write,
                    FileShare.ReadWrite,
                    FileOptions.None);
            }
            else
            {
                string partPath = GetPartFilePath(targetFile.AbsolutePath);
                EnsureParentDirectoryExists(partPath);

                using SafeFileHandle handle = File.OpenHandle(
                    partPath,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.ReadWrite,
                    FileOptions.Asynchronous);

                if (!DeviceIoControl(handle, FSCTL_SET_SPARSE, nint.Zero, 0, nint.Zero, 0, out _, nint.Zero))
                {
                    int errorCode = Marshal.GetLastPInvokeError();
                    if (errorCode != ERROR_INVALID_FUNCTION && errorCode != ERROR_NOT_SUPPORTED)
                    {
                        throw new Win32Exception(errorCode);
                    }
                }

                RandomAccess.SetLength(handle, entry.FileSize.Bytes);
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

        using SafeFileHandle handle = File.OpenHandle(
            effectivePath,
            FileMode.OpenOrCreate,
            FileAccess.Write,
            FileShare.ReadWrite,
            FileOptions.Asynchronous);

        await RandomAccess.WriteAsync(handle, data.Memory, fileOffset.Offset, ct).ConfigureAwait(false);
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

        using SafeFileHandle handle = File.OpenHandle(
            effectivePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite,
            FileOptions.Asynchronous);

        byte[] buffer = new byte[length.Bytes];
        int totalRead = 0;
        while (totalRead < length.Bytes)
        {
            int read = await RandomAccess.ReadAsync(
                handle,
                buffer.AsMemory(totalRead, length.Bytes - totalRead),
                fileOffset.Offset + totalRead,
                ct).ConfigureAwait(false);

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

        using SafeFileHandle handle = File.OpenHandle(
            slicePath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.Read,
            FileOptions.Asynchronous);

        await RandomAccess.WriteAsync(handle, data.Memory, 0, ct).ConfigureAwait(false);
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

        using SafeFileHandle handle = File.OpenHandle(
            slicePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite,
            FileOptions.Asynchronous);

        byte[] buffer = new byte[length.Bytes];
        int totalRead = 0;
        while (totalRead < length.Bytes)
        {
            int read = await RandomAccess.ReadAsync(
                handle,
                buffer.AsMemory(totalRead, length.Bytes - totalRead),
                totalRead,
                ct).ConfigureAwait(false);

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

    public ValueTask<FileMetadataResult> GetFileMetadataAsync(ResolvedFilePath sourcePath, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(sourcePath);

        FileInfo info = new(sourcePath.AbsolutePath);
        if (!info.Exists)
        {
            return ValueTask.FromResult(FileMetadataResult.NotFound());
        }

        return ValueTask.FromResult(FileMetadataResult.Found(
            FileSizeBytes.FromBytes(info.Length),
            FileLastModifiedUtc.FromDateTimeOffset(info.LastWriteTimeUtc)));
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

        string[] segments = rawRelative.Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries);
        foreach (string segment in segments)
        {
            if (segment.Contains(':'))
            {
                throw new DomainValidationException($"Relative path segment '{segment}' contains illegal Alternate Data Stream separator ':'.");
            }

            if (segment.EndsWith('.') || segment.EndsWith(' '))
            {
                throw new DomainValidationException($"Relative path segment '{segment}' cannot end with a dot or whitespace.");
            }

            int dotIndex = segment.IndexOf('.');
            string baseName = dotIndex >= 0 ? segment[..dotIndex] : segment;
            baseName = baseName.TrimEnd();

            if (ReservedDeviceNames.Contains(baseName))
            {
                throw new DomainValidationException($"Relative path segment '{segment}' uses reserved Win32 DOS device name '{baseName}'.");
            }
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

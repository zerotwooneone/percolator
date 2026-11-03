# Percolator.Infrastructure.Windows — Architecture & Implementation Plan

## Refinement Status Gate
- [x] Lens 1: Scope & Feature Slice Framing (Status: Approved)
- [x] Lens 2: Onion Architecture & Primitive Obsession (Status: Approved)
- [x] Lens 3: Threat Modeling & Security Posture (Status: Approved)
- [x] Lens 4: High-Value, Low-Brittle Unit Test Design (Status: Approved)
- [x] Lens 5: Cross-Layer Infrastructure Alignment (Status: Approved)
- [x] Implementation Gate: UNLOCKED

---

## 1. Architectural Vision & Platform Boundary

`Percolator.Infrastructure.Windows` is the dedicated Windows OS-specific infrastructure overlay assembly (`net10.0-windows`) in the Percolator Onion Architecture. While `Percolator.Infrastructure` (`net10.0`) provides 100% portable, cross-platform implementations for all infrastructure ports, Windows desktop deployments require specific OS kernel and cryptographic subsystem integrations to operate securely and without performance degradation:

1. **Windows SChannel TLS Ephemeral Key Persistence (`WindowsSChannelCertificateProvider`)** — *Implemented*:
   - Windows SChannel (`sspicli.dll` / `lsass.exe`) executes TLS handshakes out-of-process and requires private keys bound to `X509Certificate2` instances to reside in a Windows CNG (Cryptography Next Generation) key container rather than ephemeral managed memory.
2. **NTFS Sparse File Pre-Allocation & Win32 Path Hardening (`WindowsSparseDesktopFileStore`)** — *Active Roadmap*:
   - Multi-gigabyte out-of-order swarm file transfers require the `FSCTL_SET_SPARSE` (`0x000900C4`) Win32 `DeviceIoControl` ioctl so setting file length via `FileStream.SetLength` completes in $O(1)$ metadata time without blocking the disk to physically zero-fill gigabytes before the first chunk arrives.
   - Enforces strict Windows kernel path sanitization against Win32 reserved device names (`CON`, `PRN`, `AUX`, `NUL`, `COM1`–`COM9`, `LPT1`–`LPT9`) and NTFS Alternate Data Streams (`:` ADS).

> [!NOTE] Vault Master Key Exclusivity (No DPAPI Key Storage)
> In accordance with the Percolator zero-persistence master key architecture, the 32-byte SQLCipher database master key is **never** written to Windows Credential Manager or DPAPI (`ProtectedData`). It is derived/unwrapped exclusively from `percolator.header` during Stage 1 unlock and held in pinned, zeroizable unmanaged memory (`IMasterKeyHandle`).

---

## 2. Completed Chunks

### Chunk 1.1: Windows SChannel Ephemeral TLS Certificate Provider (Completed)
- **Status:** Implemented & Verified.
- **Artifacts:**
  - Adapter: `Percolator.Infrastructure.Windows.Security.WindowsSChannelCertificateProvider` implementing `Percolator.Infrastructure.Transport.ITransportCertificateProvider`.
  - DI Overrides: `Percolator.Infrastructure.Windows.DependencyInjection.WindowsInfrastructureServiceCollectionExtensions.AddWindowsInfrastructureOverrides`.
  - Unit Tests: `Percolator.Infrastructure.Windows.Tests.Security.WindowsSChannelCertificateProviderTests` (3/3 passing).
  - Integration Tests: `Percolator.Infrastructure.Windows.Tests.Transport.WindowsPeerGrpcTofuRoundTripTests` (1/1 passing).
- **Invariants Realized:**
  - [`INV-ZERO-ORPHANED-OS-KEYS`](Design.md#2-architectural-invariants-and-encapsulation): Deterministic two-phase cleanup deletes `.pfx` file in `finally` and explicitly calls `cngKey.Key.Delete()` upon rotation and disposal. Startup sweep clears orphaned temporary files.

---

## 3. Active Implementation Chunks

### Chunk 1.2: NTFS Sparse Desktop File Store & Win32/ADS Sanitization

#### Technical Specifications
- **Port Contract (`Percolator.Apps.FileTransfer.Ports.IDesktopFileStorage`)**:
  - Implements all 10 port methods defined in `Percolator.Apps.FileTransfer.Ports.IDesktopFileStorage`:
    1. `PreAllocateFilesAsync(RootDirectoryPath rootDirectory, TransferManifest manifest, IReadOnlySet<FileIndex> selectedFileIndices, CancellationToken ct = default)`
    2. `WriteChunkAsync(ResolvedFilePath partFilePath, FileByteOffset fileOffset, PlaintextChunkData data, CancellationToken ct = default)`
    3. `ReadChunkAsync(ResolvedFilePath sourceFilePath, FileByteOffset fileOffset, ByteCount length, CancellationToken ct = default)`
    4. `WriteBoundarySliceAsync(TransferSessionId sessionId, ChunkIndex chunkIndex, FileIndex fileIndex, PlaintextChunkData sliceData, CancellationToken ct = default)`
    5. `ReadBoundarySliceAsync(TransferSessionId sessionId, ChunkIndex chunkIndex, FileIndex fileIndex, CancellationToken ct = default)`
    6. `DeleteBoundaryPartsAsync(TransferSessionId sessionId, ChunkIndex chunkIndex, CancellationToken ct = default)`
    7. `ScanAndComputeBitfieldAsync(RootDirectoryPath rootDirectory, TransferManifest manifest, IReadOnlySet<FileIndex> selectedFileIndices, CancellationToken ct = default)`
    8. `FinalizeFileAsync(ResolvedFilePath partFilePath, ResolvedFilePath finalFilePath, UnixTimestampSeconds lastModifiedUtc, CancellationToken ct = default)`
    9. `CheckDiskSpaceAsync(RootDirectoryPath rootDirectory, ByteCount requiredBytes, CancellationToken ct = default)`
    10. `GetFileMetadataAsync(ResolvedFilePath filePath, CancellationToken ct = default)`

- **Adapter (`Percolator.Infrastructure.Windows.Storage.WindowsSparseDesktopFileStore`)**:
  - **Win32 Reserved Device Name & NTFS Alternate Data Stream (ADS) Sanitization**:
    - Validates every relative path segment in `TransferManifest.Files` before any disk operation:
      - Rejects NTFS Alternate Data Streams (any segment containing `:`) and directory traversal (`..`) or root-escape paths (`Path.GetFullPath(resolvedPath)` must remain strictly inside `Path.GetFullPath(rootDirectory.Value)`).
      - Strips extensions per path segment (`Path.GetFileNameWithoutExtension(segment)`) and rejects Win32 reserved DOS device names case-insensitively (`CON`, `PRN`, `AUX`, `NUL`, `COM1`–`COM9`, `LPT1`–`LPT9`, plus ISO superscript variants `COM¹`, `COM²`, `COM³`, `LPT¹`, `LPT²`, `LPT³`), returning a structured `DomainError`.
  - **Win32 `FSCTL_SET_SPARSE` Pre-Allocation**:
    - For non-zero selected files in `PreAllocateFilesAsync`, opens/creates `{relativePath}.percolator-part` and invokes P/Invoke `DeviceIoControl` with `FSCTL_SET_SPARSE = 0x000900C4` on `FileStream.SafeFileHandle` prior to calling `FileStream.SetLength(fileEntry.FileSizeBytes.Value)`:
      ```csharp
      private const uint FSCTL_SET_SPARSE = 0x000900C4;

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
      ```
    - If the underlying volume does not support sparse files (e.g., FAT32/exFAT returning `ERROR_INVALID_FUNCTION`), gracefully falls back to standard `FileStream.SetLength` without failing the transfer.
  - **Zero-Byte File Fast Path & Atomic Finalization**:
    - Zero-byte files (`FileSizeBytes == 0`) materialize immediately at their final destination path without creating a `.percolator-part` file.
    - `FinalizeFileAsync` atomically moves `{file}.percolator-part` to `{file}` via `File.Move(partPath, finalPath, overwrite: true)` and applies `File.SetLastWriteTimeUtc`.
  - **Random-Access Unbuffered Chunk I/O**:
    - `WriteChunkAsync` and `ReadChunkAsync` use `RandomAccess.WriteAsync(SafeFileHandle, ReadOnlyMemory<byte>, long fileOffset, CancellationToken)` and `RandomAccess.ReadAsync` for thread-safe, lock-free concurrent swarm chunk writes across parallel streams.

#### Scope & Deliverables
- [ ] Implement `WindowsSparseDesktopFileStore` (`Percolator.Apps.FileTransfer.Ports.IDesktopFileStorage`).

#### Embedded Unit Tests (`Percolator.Infrastructure.Windows.Tests/Storage/`)
- `WindowsSparseDesktopFileStoreTests.Sanitization_RejectsReservedWin32DeviceNamesAndNtfsAds`: verifies paths containing `CON`, `NUL.txt`, `sub/COM1.log`, `file.txt:secret:$DATA`, or `../escape` return failure without touching disk.
- `WindowsSparseDesktopFileStoreTests.PreAllocateFilesAsync_SetsSparseAttributeOnNtfs`: pre-allocates a `.percolator-part` file on NTFS and asserts `(File.GetAttributes(path) & FileAttributes.SparseFile) != 0` and logical length matches manifest size.
- `WindowsSparseDesktopFileStoreTests.ConcurrentWriteChunkAndScanBitfield_VerifiesMerkleLeaves`: writes chunks out-of-order via `WriteChunkAsync`, runs `ScanAndComputeBitfieldAsync`, and verifies the resulting bitfield and `FinalizeFileAsync` atomic rename.

#### Verification Gate
- `dotnet test Percolator.Infrastructure.Windows.Tests --filter FullyQualifiedName~Storage`

---

### Chunk 1.3: Windows DI Composition Overrides & Verification

#### Technical Specifications
- **Extension Method (`Percolator.Infrastructure.Windows.DependencyInjection.WindowsInfrastructureServiceCollectionExtensions`)**:
  - Update `AddWindowsInfrastructureOverrides` to register `WindowsSparseDesktopFileStore`:
    ```csharp
    services.Replace(ServiceDescriptor.Singleton<IDesktopFileStorage, WindowsSparseDesktopFileStore>());
    ```
- **Scope & Deliverables**:
  - [x] Scaffold `Percolator.Infrastructure.Windows.Tests` (`net10.0-windows`) and add to `Percolator.sln`.
  - [x] Implement base `AddWindowsInfrastructureOverrides` (registering `WindowsSChannelCertificateProvider`).
  - [ ] Register `WindowsSparseDesktopFileStore` in `AddWindowsInfrastructureOverrides`.

#### Embedded Unit Tests (`Percolator.Infrastructure.Windows.Tests/Composition/`)
- [ ] `WindowsDependencyInjectionTests.AddWindowsInfrastructureOverrides_SwapsFileStoreAndCertificateProvider`: registers portable defaults followed by `AddWindowsInfrastructureOverrides()`; asserts `IDesktopFileStorage` resolves to `WindowsSparseDesktopFileStore` and `ITransportCertificateProvider` resolves to `WindowsSChannelCertificateProvider`.

#### Verification Gate
- `dotnet test Percolator.Infrastructure.Windows.Tests --filter FullyQualifiedName~Composition`

---

## 4. Port-to-Adapter Realization Matrix (Windows Overlay)

| # | Port (Interface) | Defining Project | Portable Default (`Percolator.Infrastructure`) | Windows Override (`Percolator.Infrastructure.Windows`) | Status |
|---|---|---|---|---|---|
| 1 | `ITransportCertificateProvider` | `Percolator.Infrastructure.Transport` | `InMemoryTransportCertificateProvider` | `WindowsSChannelCertificateProvider` | **Implemented & Verified** (Chunk 1.1) |
| 2 | `IDesktopFileStorage` | `Percolator.Apps.FileTransfer.Ports` | `PortableDesktopFileStore` | `WindowsSparseDesktopFileStore` | **Pending** (Chunk 1.2) |

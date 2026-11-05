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

`Percolator.Infrastructure.Windows` is the dedicated Windows OS-specific infrastructure overlay assembly (`net10.0-windows10.0.19041.0`) in the Percolator Onion Architecture. While `Percolator.Infrastructure` (`net10.0`) provides 100% portable, cross-platform implementations for all infrastructure ports, Windows desktop deployments require specific OS kernel and cryptographic subsystem integrations to operate securely and without performance degradation:

1. **Windows SChannel TLS Ephemeral Key Persistence (`WindowsSChannelCertificateProvider`)** — *Implemented*:
   - Windows SChannel (`sspicli.dll` / `lsass.exe`) executes TLS handshakes out-of-process and requires private keys bound to `X509Certificate2` instances to reside in a Windows CNG (Cryptography Next Generation) key container rather than ephemeral managed memory.
2. **NTFS Sparse File Pre-Allocation & Win32 Path Hardening (`WindowsSparseDesktopFileStore`)** — *Active Roadmap*:
   - Multi-gigabyte out-of-order swarm file transfers require the `FSCTL_SET_SPARSE` (`0x000900C4`) Win32 `DeviceIoControl` ioctl so setting file length via `RandomAccess.SetLength` completes in $O(1)$ metadata time without blocking the disk to physically zero-fill gigabytes before the first chunk arrives.
   - Enforces strict Windows kernel path sanitization against Win32 reserved device names (`CON`, `PRN`, `AUX`, `NUL`, `COM1`–`COM9`, `LPT1`–`LPT9`, and superscript variants `COM¹`–`COM³`, `LPT¹`–`LPT³`), trailing dots/spaces, and NTFS Alternate Data Streams (`:` ADS), throwing domain exceptions (`DomainValidationException`) before any filesystem operations occur.

> [!NOTE] Vault Master Key Exclusivity (No DPAPI Key Storage)
> In accordance with the Percolator zero-persistence master key architecture, the 32-byte SQLCipher database master key is **never** written to Windows Credential Manager or DPAPI (`ProtectedData`). It is derived/unwrapped exclusively from `percolator.header` during Stage 1 unlock and held in pinned, zeroizable unmanaged memory (`IMasterKeyHandle`).

### 1.1 Dependency Graph & Target Framework

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0-windows10.0.19041.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <AllowUnsafeBlocks>true</AllowUnsafeBlocks>
    <SupportedOSPlatformVersion>10.0.19041.0</SupportedOSPlatformVersion>
  </PropertyGroup>

  <ItemGroup>
    <ProjectReference Include="..\Percolator.Domain\Percolator.Domain.csproj" />
    <ProjectReference Include="..\Percolator.Application\Percolator.Application.csproj" />
    <ProjectReference Include="..\Percolator.Apps.FileTransfer\Percolator.Apps.FileTransfer.csproj" />
    <ProjectReference Include="..\Percolator.Apps.FileTransfer.Domain\Percolator.Apps.FileTransfer.Domain.csproj" />
    <ProjectReference Include="..\Percolator.Infrastructure\Percolator.Infrastructure.csproj" />
  </ItemGroup>
</Project>
```

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
    1. `ValueTask PreAllocateFilesAsync(RootDirectoryPath rootPath, TransferManifest manifest, SelectedFileIndices selectedIndices, CancellationToken ct = default)`
    2. `ValueTask WriteChunkAsync(ResolvedFilePath targetPath, FileByteOffset fileOffset, PlaintextChunkData data, CancellationToken ct = default)`
    3. `ValueTask<PlaintextChunkData> ReadChunkAsync(ResolvedFilePath sourcePath, FileByteOffset fileOffset, TransferByteLength length, CancellationToken ct = default)`
    4. `ValueTask WriteBoundarySliceAsync(TransferSessionId sessionId, ChunkIndex chunkIndex, FileIndex fileIndex, PlaintextChunkData data, CancellationToken ct = default)`
    5. `ValueTask<PlaintextChunkData?> ReadBoundarySliceAsync(TransferSessionId sessionId, ChunkIndex chunkIndex, FileIndex fileIndex, TransferByteLength length, CancellationToken ct = default)`
    6. `ValueTask DeleteBoundaryPartsAsync(TransferSessionId sessionId, CancellationToken ct = default)`
    7. `ValueTask<Bitfield> ScanAndComputeBitfieldAsync(RootDirectoryPath rootPath, TransferManifest manifest, SelectedFileIndices? selectedIndices = null, CancellationToken ct = default)`
    8. `ValueTask FinalizeFileAsync(ResolvedFilePath targetPath, FileLastModifiedUtc lastModifiedUtc, CancellationToken ct = default)`
    9. `ValueTask<DiskSpacePreflightResult> CheckDiskSpaceAsync(RootDirectoryPath rootPath, TotalBundleBytes requiredBytes, CancellationToken ct = default)`
    10. `ValueTask<FileMetadataResult> GetFileMetadataAsync(ResolvedFilePath sourcePath, CancellationToken ct = default)`

- **Adapter (`Percolator.Infrastructure.Windows.Storage.WindowsSparseDesktopFileStore`)**:
  - **Constructor & Partials Storage**:
    - Accepts optional `string? partialsBaseDirectory = null`.
    - Defaults to `Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Percolator", "Partials")`.
  - **Win32 Reserved Device Name & NTFS Alternate Data Stream (ADS) Sanitization**:
    - Validates every relative path in `manifest.Files` before any disk I/O, throwing `DomainValidationException` on violation:
      - **NTFS ADS Guard**: Rejects any path segment containing a colon (`:`), such as `file.txt:secret` or `test::$DATA`.
      - **Directory Traversal Guard**: Rejects `..`, null bytes (`\0`), leading separators (`/` or `\`), and verifies canonical root containment (`Path.GetFullPath(resolvedPath).StartsWith(canonicalRoot + Path.DirectorySeparatorChar)`).
      - **Win32 DOS Reserved Device Name Guard**: Extracts the base name before any extension for each path segment (e.g. `CON.tar.gz` $\rightarrow$ `CON`, `aux.txt` $\rightarrow$ `aux`) and checks case-insensitively against DOS devices:
        - `CON`, `PRN`, `AUX`, `NUL`
        - `COM1` through `COM9`, plus superscript variants `COM¹`, `COM²`, `COM³`
        - `LPT1` through `LPT9`, plus superscript variants `LPT¹`, `LPT²`, `LPT³`
      - **Trailing Dot / Whitespace Guard**: Rejects path segments ending with a dot (`.`) or space (` `), which Win32 silently normalizes or rejects.
  - **Win32 `FSCTL_SET_SPARSE` Pre-Allocation**:
    - For non-zero selected files in `PreAllocateFilesAsync`:
      - Derives staging path: `string partPath = $"{targetPath.AbsolutePath}.percolator-part"`.
      - Opens or creates the file handle via:
        ```csharp
        using SafeFileHandle handle = File.OpenHandle(
            partPath,
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.ReadWrite,
            FileOptions.Asynchronous);
        ```
      - Invokes Win32 P/Invoke `DeviceIoControl` with `FSCTL_SET_SPARSE = 0x000900C4`:
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
      - Calls `DeviceIoControl(handle, FSCTL_SET_SPARSE, nint.Zero, 0, nint.Zero, 0, out _, nint.Zero)`.
      - If `DeviceIoControl` returns `false`:
        - Inspects `Marshal.GetLastPInvokeError()`.
        - If error is `1` (`ERROR_INVALID_FUNCTION`) or `50` (`ERROR_NOT_SUPPORTED`) (indicating a volume like FAT32 or exFAT that does not support sparse files), gracefully ignores and falls back to standard file sizing.
        - Otherwise, throws `new Win32Exception(Marshal.GetLastPInvokeError())`.
      - Sets the pre-allocated logical file length via `RandomAccess.SetLength(handle, entry.FileSize.Bytes)`.
  - **Zero-Byte File Fast Path & Atomic Finalization**:
    - Files with `entry.FileSize.Bytes == 0` materialize immediately at `targetPath.AbsolutePath` using an empty handle without creating a `.percolator-part` file.
    - `FinalizeFileAsync` checks for `{targetPath.AbsolutePath}.percolator-part`. If present, deletes any existing target file, executes atomic rename `File.Move(partPath, targetPath.AbsolutePath, overwrite: true)`, and stamps `lastModifiedUtc.UtcDateTime.UtcDateTime` via `File.SetLastWriteTimeUtc`.
  - **Thread-Safe Positional Unbuffered Chunk I/O via `RandomAccess`**:
    - Swarm downloads write and read chunks out of order across concurrent parallel worker tasks.
    - `WriteChunkAsync`:
      - Resolves part path or final path (`File.Exists(partPath) ? partPath : targetPath.AbsolutePath`).
      - Opens file handle with `FileAccess.Write`, `FileShare.ReadWrite`, and `FileOptions.Asynchronous`.
      - Writes directly at positional offset:
        ```csharp
        await RandomAccess.WriteAsync(handle, data.Memory, fileOffset.Offset, ct).ConfigureAwait(false);
        ```
      - Eliminates `FileStream.Seek()` mutation races, allowing safe concurrent multi-stream writes to disjoint offsets of the same file handle.
    - `ReadChunkAsync`:
      - Resolves effective path; throws `FileNotFoundException` if neither target nor part file exists.
      - Allocates `new byte[length.Bytes]`.
      - Reads positional slice via `await RandomAccess.ReadAsync(handle, buffer.AsMemory(0, length.Bytes), fileOffset.Offset, ct)`.
      - Returns `PlaintextChunkData.FromBytes(buffer)`.
  - **Boundary Slices & Bitfield Verification**:
    - `WriteBoundarySliceAsync` / `ReadBoundarySliceAsync`: Persists cross-file boundary slices in `{_boundaryPartialsBaseDirectory}/{sessionId:N}/{chunkIndex}_{fileIndex}.slice` using `RandomAccess`.
    - `DeleteBoundaryPartsAsync`: Recursively removes `{_boundaryPartialsBaseDirectory}/{sessionId:N}`.
    - `ScanAndComputeBitfieldAsync`: Computes Merkle leaf hashes for all chunks across the bundle layout using `ChunkBoundaryCalculator.CalculateSlices` and `MerkleTreeBuilder.HashLeaf`, returning an updated `Bitfield`.

#### Scope & Deliverables
- [ ] Implement `WindowsSparseDesktopFileStore` (`Percolator.Apps.FileTransfer.Ports.IDesktopFileStorage`) in `Percolator.Infrastructure.Windows.Storage`.

#### Embedded Unit Tests (`Percolator.Infrastructure.Windows.Tests/Storage/`)
- `WindowsSparseDesktopFileStoreTests.Sanitization_RejectsReservedWin32DeviceNamesAndNtfsAds`:
  - Asserts that paths containing `CON`, `NUL.txt`, `sub/COM1.log`, `file.txt:secret:$DATA`, `trailing./test`, `space /test`, or `../escape` throw `DomainValidationException` without touching disk.
- `WindowsSparseDesktopFileStoreTests.PreAllocateFilesAsync_SetsSparseAttributeOnNtfs`:
  - Pre-allocates a `.percolator-part` file on an NTFS drive; asserts that `(File.GetAttributes(partPath) & FileAttributes.SparseFile) != 0` and `new FileInfo(partPath).Length == expectedBytes`.
- `WindowsSparseDesktopFileStoreTests.HappyPath_WriteAndReadChunk_MatchesExactBytes`:
  - **Mandatory Happy-Path Round-Trip Test** (Workflow A, Cardinal Principle 4): Pre-allocates file, writes multiple chunks concurrently via `WriteChunkAsync`, reads chunks back via `ReadChunkAsync`, finalizes with `FinalizeFileAsync`, and asserts byte-level equivalence.
- `WindowsSparseDesktopFileStoreTests.BoundarySlice_WriteAndReadForUnselectedFile_PersistsAndCleansUp`:
  - Tests boundary slice staging and deletion for multi-file swarm boundaries.
- `WindowsSparseDesktopFileStoreTests.ConcurrentWriteChunkAndScanBitfield_VerifiesMerkleLeaves`:
  - Writes out-of-order chunks across boundary files and verifies that `ScanAndComputeBitfieldAsync` produces a matching `Bitfield`.

#### Verification Gate
- `dotnet test Percolator.Infrastructure.Windows.Tests --filter FullyQualifiedName~Storage`

---

### Chunk 1.3: Windows DI Composition Overrides & Verification

#### Technical Specifications
- **Extension Method (`Percolator.Infrastructure.Windows.DependencyInjection.WindowsInfrastructureServiceCollectionExtensions`)**:
  - Update `AddWindowsInfrastructureOverrides` to cleanly replace portable defaults using `Microsoft.Extensions.DependencyInjection.Extensions.ServiceCollectionDescriptorExtensions.Replace`:
    ```csharp
    namespace Percolator.Infrastructure.Windows.DependencyInjection;

    using System.Runtime.Versioning;
    using Microsoft.Extensions.DependencyInjection;
    using Microsoft.Extensions.DependencyInjection.Extensions;
    using Percolator.Apps.FileTransfer.Ports;
    using Percolator.Infrastructure.Transport;
    using Percolator.Infrastructure.Windows.Security;
    using Percolator.Infrastructure.Windows.Storage;

    [SupportedOSPlatform("windows")]
    public static class WindowsInfrastructureServiceCollectionExtensions
    {
        public static IServiceCollection AddWindowsInfrastructureOverrides(this IServiceCollection services)
        {
            services.Replace(ServiceDescriptor.Singleton<ITransportCertificateProvider, WindowsSChannelCertificateProvider>());
            services.Replace(ServiceDescriptor.Singleton<IDesktopFileStorage, WindowsSparseDesktopFileStore>());
            return services;
        }
    }
    ```
- **Scope & Deliverables**:
  - [x] Scaffold `Percolator.Infrastructure.Windows.Tests` (`net10.0-windows10.0.19041.0`) and add to `Percolator.sln`.
  - [x] Implement base `AddWindowsInfrastructureOverrides` (registering `WindowsSChannelCertificateProvider`).
  - [ ] Update `AddWindowsInfrastructureOverrides` to use `services.Replace(...)` and register `WindowsSparseDesktopFileStore`.

#### Embedded Unit Tests (`Percolator.Infrastructure.Windows.Tests/Composition/`)
- [ ] `WindowsDependencyInjectionTests.AddWindowsInfrastructureOverrides_SwapsFileStoreAndCertificateProvider`:
  - Registers portable defaults via `services.AddAuthenticatedInfrastructure(...)` followed by `services.AddWindowsInfrastructureOverrides()`.
  - Asserts that `sp.GetRequiredService<IDesktopFileStorage>()` resolves to `WindowsSparseDesktopFileStore`.
  - Asserts that `sp.GetRequiredService<ITransportCertificateProvider>()` resolves to `WindowsSChannelCertificateProvider`.
  - Asserts that single instance resolution does not resolve duplicate descriptors.

#### Verification Gate
- `dotnet test Percolator.Infrastructure.Windows.Tests --filter FullyQualifiedName~Composition`

---

## 4. Port-to-Adapter Realization Matrix (Windows Overlay)

| # | Port (Interface) | Defining Project | Portable Default (`Percolator.Infrastructure`) | Windows Override (`Percolator.Infrastructure.Windows`) | Status |
|---|---|---|---|---|---|
| 1 | `ITransportCertificateProvider` | `Percolator.Infrastructure.Transport` | `InMemoryTransportCertificateProvider` | `WindowsSChannelCertificateProvider` | **Implemented & Verified** (Chunk 1.1) |
| 2 | `IDesktopFileStorage` | `Percolator.Apps.FileTransfer.Ports` | `PortableDesktopFileStore` | `WindowsSparseDesktopFileStore` | **Pending** (Chunk 1.2) |

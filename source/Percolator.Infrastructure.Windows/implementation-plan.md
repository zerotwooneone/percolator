# Percolator.Infrastructure.Windows Implementation Plan

## 1. Overview & Architectural Role

`Percolator.Infrastructure.Windows` is a dedicated platform-specific adapter assembly targeting `net10.0-windows`. 

Following Clean Architecture & Onion Architecture principles:
- **Plug-in Platform Adapters**: This library realizes ports defined in `Percolator.Domain`, `Percolator.Application`, and `Percolator.Apps.FileTransfer` that require native Windows APIs, Windows Security subsystems, or Windows kernel IOCTLs.
- **Isolation of Windows Dependencies**: Core `Percolator.Infrastructure2` targets standard `net10.0` and remains 100% portable for headless Linux Docker relays, macOS, and Linux desktop clients. All Win32 P/Invokes, SChannel/CNG handles, NTFS IOCTLs, and DPAPI calls are isolated exclusively to this project.
- **Zero Roslyn CA1416 Noise**: Targeting `net10.0-windows` ensures that Windows-specific APIs compile cleanly without compiler warnings or repetitive runtime platform guard checks.

---

## 2. Swappable Interfaces & Windows-Specific Adapters

To enable seamless swapping between cross-platform baseline behaviors and Windows-native performance optimizations, the system defines three swappable ports:

```
┌────────────────────────────────────────────────────────┐
│                   Ports (Interfaces)                   │
├──────────────────────────┬─────────────────────────────┤
│ ICredentialStorage       │ ITransportCertificateProvider
│ IDesktopFileStorage      │                             │
└────────────┬─────────────┴───────────────┬─────────────┘
             │                             │
             ▼                             ▼
┌──────────────────────────┐  ┌──────────────────────────┐
│ Percolator.Infrastructure2│  │Percolator.Infrastructure │
│    (Portable net10.0)    │  │       .Windows           │
│                          │  │  (net10.0-windows)       │
├──────────────────────────┤  ├──────────────────────────┤
│• PortablePassphraseStore │  │• WindowsDpapiCredential- │
│  (Argon2id + keyfile)    │  │  Storage                 │
│• InMemoryCertProvider    │  │• WindowsSChannel-        │
│  (OpenSSL in-memory)     │  │  CertificateProvider     │
│• PortableDesktopFileStore│  │• WindowsSparseDesktop-   │
│  (FileStream.SetLength)  │  │  FileStore (FSCTL_SPARSE)│
└──────────────────────────┘  └──────────────────────────┘
```

---

### 2.1 Credential Storage & Database Protection

#### Port: `ICredentialStorage` (`Percolator.Domain.Security.Ports` or `Percolator.Application.Ports`)
```csharp
public interface ICredentialStorage
{
    Task StoreSecretAsync(string key, ReadOnlyMemory<byte> secret, CancellationToken ct = default);
    Task<byte[]?> RetrieveSecretAsync(string key, CancellationToken ct = default);
    Task DeleteSecretAsync(string key, CancellationToken ct = default);
}
```

#### Windows Adapter: `WindowsDpapiCredentialStorage`
- **DPAPI Encryption**: Protects master database encryption keys (used by SQLCipher) and root identity seed keys using Windows DPAPI:
  ```csharp
  ProtectedData.Protect(secretBytes, optionalEntropy, DataProtectionScope.CurrentUser);
  ProtectedData.Unprotect(encryptedBytes, optionalEntropy, DataProtectionScope.CurrentUser);
  ```
- **Strict Filesystem ACL Hardening**:
  - Encrypted master key blobs stored on disk (`%LocalAppData%\Percolator\Vault\master.key.dat`) are secured with explicit Windows NTFS ACLs:
  - Grants `FileSystemRights.FullControl` exclusively to `WindowsIdentity.GetCurrent().User`.
  - Disables rule inheritance (`SetAccessRuleProtection(isProtected: true, preserveInheritance: false)`), stripping permissions from all other local non-admin accounts.

---

### 2.2 SChannel-Resilient Ephemeral TLS 1.3 Transport

#### Port: `ITransportCertificateProvider` (`Percolator.Application.Ports`)
```csharp
public interface ITransportCertificateProvider
{
    Task<X509Certificate2> GetOrCreateServerCertificateAsync(CancellationToken ct = default);
    Task RotateCertificateAsync(CancellationToken ct = default);
}
```

#### Windows Problem Statement:
Windows SChannel fails incoming TLS 1.3 server handshakes when using ephemeral in-memory certificates created via `X509Certificate2.CreateSelfSigned`. SChannel requires the certificate and private key to be persisted into a Windows cryptographic key storage provider on disk. Furthermore, writing PKCS#12 files triggers background Windows Defender scans that hold shared locks, and rotating certificates leaks unmanaged keys into the Windows CNG key store.

#### Windows Adapter: `WindowsSChannelCertificateProvider`
- **ECDSA P-256 Keypair Generation**: Generates disposable ECDSA P-256 (`ECCurve.NamedCurves.nistP256`) self-signed certificate with `CN=Percolator-Ephemeral`, `KeyUsageFlags.DigitalSignature`, and loopback SANs.
- **PKCS#12 Disk Export & CNG Key Set Persistence**:
  - Exports certificate to temporary `.pfx` bytes.
  - Loads via:
    ```csharp
    X509CertificateLoader.LoadPkcs12FromFile(
        pfxPath,
        password,
        X509KeyStorageFlags.Exportable | X509KeyStorageFlags.PersistKeySet);
    ```
- **Antivirus Write-Retry Resilience**:
  - Implements exponential backoff with jitter on `IOException` (`WriteCertificateWithRetryAsync`) when writing `.pfx` files to overcome transient Windows Defender file sharing locks.
- **Unmanaged CNG Container Cleanup**:
  - On certificate rotation or node shutdown, explicitly invokes:
    ```csharp
    if (cert.GetECDsaPrivateKey() is ECDsaCng cngKey)
    {
        cngKey.Key.Delete();
    }
    ```
  - Purges ephemeral keys from the Windows CNG machine/user key store, preventing cryptographic handle leaks.

---

### 2.3 NTFS Sparse File Storage Engine

#### Port: `IDesktopFileStorage` (`Percolator.Apps.FileTransfer.Ports`)
```csharp
public interface IDesktopFileStorage
{
    Task<SafeFileHandle> PreAllocateTargetFileAsync(string relativePath, long totalBytes, CancellationToken ct = default);
    Task WriteChunkAsync(SafeFileHandle fileHandle, long fileOffset, ReadOnlyMemory<byte> chunkData, CancellationToken ct = default);
    Task FinalizeFileAsync(string stagingPath, string finalPath, DateTimeOffset manifestTimestampUtc, CancellationToken ct = default);
    Task<Bitfield> ScanResumptionStateAsync(string filePath, int chunkSize, IReadOnlyList<byte[]> expectedChunkHashes, CancellationToken ct = default);
}
```

#### Windows Optimization:
Writing multi-gigabyte files chunk-by-chunk in a swarm without pre-allocation causes massive NTFS fragmentation. Standard pre-allocation (`FileStream.SetLength`) on Windows forces the OS kernel to physically write zeroes across the allocated blocks, blocking download initialization.

#### Windows Adapter: `WindowsSparseDesktopFileStore`
- **NTFS Sparse Allocation via `DeviceIoControl`**:
  - Opens target file with `FileShare.ReadWrite | FileShare.Delete`.
  - Sends Win32 `FSCTL_SET_SPARSE` (IOCTL `0x000900C4`) via `DeviceIoControl`:
    ```csharp
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(
        SafeFileHandle hDevice,
        uint dwIoControlCode, // 0x000900C4 (FSCTL_SET_SPARSE)
        IntPtr lpInBuffer,
        uint nInBufferSize,
        IntPtr lpOutBuffer,
        uint nOutBufferSize,
        out uint lpBytesReturned,
        IntPtr lpOverlapped);
    ```
  - Extends file pointer via `SetFilePointerEx` / `SetEndOfFile` (or `FileStream.SetLength`).
  - Result: Multi-gigabyte sparse containers are allocated in < 1 millisecond with zero disk I/O and zero fragmentation.
- **`.percolator-part` Staging**:
  - Writes incoming chunks to `{relativePath}.percolator-part` using `RandomAccess.WriteAsync(SafeFileHandle, ReadOnlyMemory<byte>, fileOffset, ct)`.
  - Prevents Windows Search Indexer and antivirus scanners from processing incomplete files.
- **Atomic Renaming & Timestamp Preservation**:
  - Atomically commits via `File.Move(stagingPath, finalPath, overwrite: true)`.
  - Restores author manifest timestamp via `File.SetLastWriteTimeUtc(finalPath, manifestTimestampUtc)`.
- **Resumption Bitfield Scanner**:
  - Re-reads existing `.percolator-part` and completed files in 1 MB blocks.
  - Computes SHA-256 hashes against manifest expected hashes to populate the Bitfield for interrupted downloads.

---

## 3. Dependency Injection & Service Registration

`Percolator.Infrastructure.Windows` provides an extension method that replaces the portable infrastructure defaults with Windows-optimized implementations:

```csharp
namespace Microsoft.Extensions.DependencyInjection;

public static class WindowsInfrastructureServiceCollectionExtensions
{
    public static IServiceCollection AddWindowsInfrastructure(this IServiceCollection services)
    {
        // Replace portable credential storage with Windows DPAPI + ACLs
        services.AddSingleton<ICredentialStorage, WindowsDpapiCredentialStorage>();

        // Replace portable in-memory cert provider with Windows SChannel/CNG provider
        services.AddSingleton<ITransportCertificateProvider, WindowsSChannelCertificateProvider>();

        // Replace portable file pre-allocator with NTFS sparse file engine
        services.AddSingleton<IDesktopFileStorage, WindowsSparseDesktopFileStore>();

        return services;
    }
}
```

In the Desktop Host (`Program.cs` / composition root):
```csharp
// Register base cross-platform infrastructure
services.AddInfrastructure2(configuration);

// Conditionally overlay Windows-specific adapters
if (OperatingSystem.IsWindows())
{
    services.AddWindowsInfrastructure();
}
```

---

## 4. Implementation Steps & Milestones

1. **Milestone 1: Windows Credential Storage (`WindowsDpapiCredentialStorage`)**
   - Implement DPAPI encryption wrapper with static entropy.
   - Implement NTFS ACL helper stripping inherited rules and restricting access to `WindowsIdentity.GetCurrent().User`.
   - Unit tests verifying round-trip encryption and ACL application.

2. **Milestone 2: Windows SChannel Transport Provider (`WindowsSChannelCertificateProvider`)**
   - Implement ECDSA P-256 keypair generation and PFX export.
   - Implement `WriteCertificateWithRetryAsync` with exponential backoff on `IOException`.
   - Implement unmanaged CNG container deletion on `Dispose` / `RotateCertificateAsync`.
   - Unit tests verifying SChannel gRPC TLS handshake compatibility.

3. **Milestone 3: NTFS Sparse File Engine (`WindowsSparseDesktopFileStore`)**
   - Implement `DeviceIoControl` P/Invoke for `FSCTL_SET_SPARSE`.
   - Implement chunk writing with `RandomAccess.WriteAsync`.
   - Implement atomic finalization and resumption bitfield calculation.
   - Unit tests verifying sparse allocation and bitfield verification.

4. **Milestone 4: Composition Root Integration**
   - Implement `WindowsInfrastructureServiceCollectionExtensions.AddWindowsInfrastructure`.
   - Verify seamless DI replacement over `Infrastructure2` baseline.

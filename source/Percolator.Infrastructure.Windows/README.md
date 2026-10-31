# Percolator.Infrastructure.Windows

`Percolator.Infrastructure.Windows` is the Windows OS-specific infrastructure overlay assembly (`net10.0-windows`) for the Percolator ecosystem. While `Percolator.Infrastructure` provides portable, cross-platform implementations for all infrastructure ports, this project provides specialized Windows kernel and cryptographic integrations.

For detailed architectural boundaries, P/Invoke signatures, and security analysis, see the project [Design Document](Design.md).

---

## Specialized Windows Adapters

### 1. Windows SChannel Ephemeral TLS Certificate Provider
- **Location**: `Security/WindowsSChannelCertificateProvider.cs`
- **Purpose**: Windows SChannel (`lsass.exe` / `sspicli.dll`) requires TLS certificates with private keys residing in a Windows Cryptography Next Generation (CNG) key container (`X509KeyStorageFlags.UserKeySet`) rather than ephemeral in-memory storage.
- **Lifecycle & Security**:
  - Generates ephemeral ECDSA P-256 self-signed certificates with loopback Subject Alternative Names.
  - Exports via PKCS#12 `.pfx` using a 32-byte CSPRNG password that is immediately zeroized in memory.
  - Implements retry with exponential backoff on file loading to handle transient Windows Defender / antivirus scanner locks.
  - Deterministically deletes the underlying `CngKey` container upon certificate rotation and disposal (`ECDsaCng.Key.Delete()`), preventing orphan keys from accumulating in `%AppData%\Microsoft\Crypto\Keys`.

### 2. NTFS Sparse Desktop File Store & Win32 Path Hardening
- **Location**: `Storage/WindowsSparseDesktopFileStore.cs`
- **Purpose**: Implements `IDesktopFileStorage` for high-throughput multi-stream file transfers on Windows NTFS/ReFS filesystems.
- **Features**:
  - **$O(1)$ Sparse Pre-Allocation**: Issues Win32 `FSCTL_SET_SPARSE` (`0x000900C4`) via `DeviceIoControl` before setting file lengths, enabling instantaneous metadata pre-allocation without blocking disk threads to zero-fill gigabytes.
  - **Win32 & ADS Path Hardening**: Validates all relative file paths against NTFS Alternate Data Streams (`:`) and case-insensitive DOS reserved device names (`CON`, `PRN`, `AUX`, `NUL`, `COM1`–`COM9`, `LPT1`–`LPT9`, and superscript variants).

---

## Dependency Injection

When running on Windows desktop, register these overrides in your composition root:

```csharp
services.AddWindowsInfrastructureOverrides();
```

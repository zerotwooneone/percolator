# Percolator.Infrastructure.Windows

Spec: [design-spec](../spec/design-spec.md)

## 1. Purpose and Layer Boundary

`Percolator.Infrastructure.Windows` is the Windows OS-specific infrastructure overlay deployable (`net10.0-windows`). Segregated from [`Percolator.Infrastructure`](../Percolator.Infrastructure/Design.md) so the core infrastructure assembly remains 100% cross-platform, it overrides portable transport certificate and file-storage adapters where Windows kernel or cryptographic subsystem semantics require native Win32 or CNG integration.

- **In Scope:** Windows SChannel CNG-backed ephemeral TLS certificate management (`ITransportCertificateProvider`), NTFS sparse file pre-allocation and Win32/ADS path hardening (`IDesktopFileStorage`), and DI override registration (`AddWindowsInfrastructureOverrides`).
- **Out of Scope:** Cross-platform SQLCipher persistence, Protobuf serialization, gRPC transport pipelines, and DPAPI master key storage.

---

## 2. Architectural Invariants and Encapsulation

1. **Strict Platform Guarding (`[SupportedOSPlatform("windows")]`):** All native P/Invoke (`[LibraryImport("kernel32.dll")]`) and Windows CNG operations are encapsulated inside internal/sealed adapters annotated with `[SupportedOSPlatform("windows")]`.
2. **Zero Orphaned OS Resources:** Every temporary `.pfx` file or Windows CNG key container created to satisfy OS subsystem requirements must be deterministically deleted and zeroized on rotation or disposal.

---

## 3. Key Technical Design Choices

1. **CNG UserKeySet Ephemeral PFX Import with Immediate Zeroization & `CngKey.Delete()`:**
   - *Design Choice:* Because Windows SChannel (`lsass.exe` / `sspicli.dll`) executes TLS handshakes out-of-process and rejects in-memory ephemeral private keys, the provider exports the generated ECDSA certificate with a CSPRNG one-time password, loads it into `X509KeyStorageFlags.UserKeySet` with retry backoff (tolerating transient antivirus scan locks), immediately zeroizes the password/PFX buffers and deletes the temp file in `finally`, and invokes `cngKey.Key.Delete()` upon rotation or disposal.
   - *Rejected Alternative:* Using `X509KeyStorageFlags.EphemeralKeySet` on Windows (which fails SChannel handshakes with `Win32Exception`) or leaving imported keys in `%AppData%\Microsoft\Crypto\Keys` without explicit CNG container deletion.
2. **Win32 `FSCTL_SET_SPARSE` Pre-Allocation over Physical Zero-Fill:**
   - *Design Choice:* Issues `DeviceIoControl(..., FSCTL_SET_SPARSE, ...)` on `.percolator-part` file handles prior to calling `FileStream.SetLength`, turning multi-gigabyte out-of-order file pre-allocation into an $O(1)$ metadata operation on NTFS/ReFS.
   - *Rejected Alternative:* Calling `SetLength` on non-sparse NTFS files, which blocks disk threads while the OS kernel physically writes gigabytes of zeros before chunk 0 can be processed.
3. **Win32 Reserved Device Name & NTFS Alternate Data Stream (ADS) Hardening:**
   - Extending the cross-platform path rules in [`Percolator.Apps.FileTransfer.Domain`](../Percolator.Apps.FileTransfer.Domain/Design.md), Windows storage rejects any path segment containing `:` (NTFS Alternate Data Streams) or matching DOS reserved device names (`CON`, `PRN`, `AUX`, `NUL`, `COM1`–`COM9`, `LPT1`–`LPT9`, and superscript variants) regardless of file extension.

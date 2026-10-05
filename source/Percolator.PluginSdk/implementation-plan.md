# Percolator.PluginSdk Implementation Plan: Encrypted Blob Storage Contracts

## 1. Overview & Architectural Role

`Percolator.PluginSdk` provides the public microkernel contracts, DTOs, interfaces, and value objects exposed to application plugins (`Percolator.Apps.Chat`, `Percolator.Apps.Discovery`, `Percolator.Apps.FileTransfer`, etc.).

Following Onion Architecture principles:
- **Dependencies Flow Inward**: `PluginSdk` depends **only** on `Percolator.Domain`.
- **Zero Technical Infrastructure**: Contains zero references to SQLite, EF Core, gRPC, HTTP, raw sockets, or operating system file systems.
- **Microkernel Contract Boundary**: Plugins consume services provided by the host application through pure C# interfaces. Inner layers (`Percolator.Application2`) implement these interfaces, and outer adapters (`Percolator.Infrastructure2`) provide technical realization.

---

## 2. Problem Definition & Contract Motivation

### 2.1 The Double Ratchet 64 KB Invariant
In `Percolator.Application2`, end-to-end encrypted direct channels and multi-party group channels enforce a strict payload boundary:
```csharp
public const int MaxPayloadBytes = 64 * 1024; // 64 KB
```
This constraint bounds in-flight message keys in memory, prevents transactional outbox bloat in SQLite, and ensures low-latency message ratchet advancement.

### 2.2 Why Not `Percolator.Apps.FileTransfer`?
`Percolator.Apps.FileTransfer` is a heavy, BitTorrent-like peer swarming network for multi-gigabyte files (Merkle trees, K-buckets, chunk negotiation). Using it for typical chat media (a 1.5 MB photo or voice note) introduces excessive latency, requires online swarms, and creates massive protocol overhead.

### 2.3 The `PluginSdk` Abstraction
`PluginSdk` defines a lightweight, single-endpoint encrypted blob transfer abstraction (`IBlobStorageService`). It enables plugins to transmit out-of-band encrypted media (1 KB to 50 MB) by embedding a featherweight (~300-byte) `BlobReference` inside the normal 64 KB E2EE ratchet envelope.

### 2.4 Separation of Responsibilities Across Projects
- **`Percolator.PluginSdk` (This Project)**: Defines the public C# types, DTOs, and `IBlobStorageService` interface consumed by plugins.
- **`Percolator.Application2`**: Implements `IBlobStorageService` via `BlobStorageService`, orchestrating channel topology routing (Direct P2P staging vs. Channel Relay upload) and local disk cache policies.
- **`Percolator.Infrastructure2`**: Implements technical ports:
  - `StreamingAesGcmCryptoService` (64 KB chunked STREAM AEAD, bucket padding, EXIF scrubbing).
  - `RelayBlobClient` (HTTP/2 or gRPC streaming with Range resumption).
  - `PeerStreamBlobClient` (multiplexed peer stream transfer).
  - `LocalFileChunkStore` (atomic disk cache in AppData).
- **`Percolator.Apps.Chat`**: Consumes `IBlobStorageService` for inline ($\le 32$ KB) vs. out-of-band ($> 32$ KB) media attachments.

---

## 3. Public Contracts & Types in `PluginSdk`

### 3.1 Blob Identification & Storage Mode
```csharp
namespace Percolator.PluginSdk;

public readonly record struct BlobId(string HexDigest)
{
    public static BlobId FromSha256(byte[] sha256Bytes)
    {
        ArgumentNullException.ThrowIfNull(sha256Bytes);
        return new BlobId(Convert.ToHexString(sha256Bytes).ToLowerInvariant());
    }

    public override string ToString() => HexDigest;
}

public enum BlobStorageMode
{
    DirectP2P = 1,
    ChannelRelay = 2
}
```

### 3.2 Telemetry: Transfer Progress
```csharp
public readonly record struct TransferProgress(
    long BytesTransferred,
    long TotalBytes,
    double BytesPerSecond)
{
    public double FractionComplete => TotalBytes > 0 ? Math.Clamp((double)BytesTransferred / TotalBytes, 0.0, 1.0) : 0.0;
}
```

### 3.3 The `BlobReference` Envelope Descriptor
```csharp
/// <summary>
/// Lightweight descriptor carried inside E2EE application payloads (e.g. Chat ChatMessageDto).
/// Fits comfortably within the 64 KB Double Ratchet payload limit.
/// </summary>
public sealed record BlobReference
{
    public required BlobId Id { get; init; }
    public required byte[] CiphertextSha256 { get; init; }
    public required byte[] EncryptionKey { get; init; }
    public required byte[] BaseNonce { get; init; }
    public required long PlaintextSizeBytes { get; init; }
    public required long CiphertextSizeBytes { get; init; }
    public required string MimeType { get; init; }
    public required DateTimeOffset ExpiresAtUtc { get; init; }
    public required BlobStorageMode StorageMode { get; init; }
    public string? RelayEndpointUri { get; init; }
    public string? BlurHash { get; init; }
    public byte[]? InlineThumbnail { get; init; }
    public byte[]? Waveform { get; init; }
    public int? Width { get; init; }
    public int? Height { get; init; }
    public int? DurationSeconds { get; init; }

    /// <summary>
    /// Explicitly sanitizes key material from diagnostic logs.
    /// </summary>
    public override string ToString() =>
        $"BlobReference {{ Id = {Id}, StorageMode = {StorageMode}, MimeType = {MimeType}, " +
        $"PlaintextSize = {PlaintextSizeBytes}, CiphertextSize = {CiphertextSizeBytes}, " +
        $"ExpiresAt = {ExpiresAtUtc:u}, EncryptionKey = [REDACTED], BaseNonce = [REDACTED] }}";
}
```

### 3.4 Upload & Download Request/Result Models
```csharp
public sealed record BlobUploadRequest(
    Stream ContentStream,
    string MimeType,
    long ContentLength,
    ChannelId ChannelId,
    TimeSpan? TimeToLive = null,
    string? BlurHash = null,
    byte[]? InlineThumbnail = null,
    byte[]? Waveform = null,
    int? Width = null,
    int? Height = null,
    int? DurationSeconds = null);

public sealed record BlobUploadResult(
    BlobReference Reference,
    BlobStorageMode StorageMode);

public sealed record BlobDownloadRequest(
    BlobReference Reference,
    CancellationToken CancellationToken = default);

public sealed record BlobDownloadResult(
    Stream ContentStream,
    string MimeType,
    long ContentLength);
```

### 3.5 Service Port: `IBlobStorageService`
```csharp
public interface IBlobStorageService
{
    /// <summary>
    /// Strips EXIF metadata, encrypts (chunked STREAM AEAD), and stages/transfers media:
    /// - 1:1 Direct (no relay): stages ciphertext in local chunk store for direct peer pull (fails if peer offline).
    /// - 1:1 Relayed or GroupChannel: uploads once to the channel's designated relay.
    /// Returns a compact BlobReference suitable for embedding in an E2EE chat message.
    /// </summary>
    ValueTask<DomainResult<BlobUploadResult>> UploadBlobAsync(
        BlobUploadRequest request,
        IProgress<TransferProgress>? progress = null,
        CancellationToken ct = default);

    /// <summary>
    /// Downloads, verifies chunk authentication tags, and decrypts the media payload.
    /// Uses local disk cache if previously downloaded. Supports byte-range resumption.
    /// </summary>
    ValueTask<DomainResult<BlobDownloadResult>> DownloadBlobAsync(
        BlobDownloadRequest request,
        IProgress<TransferProgress>? progress = null,
        CancellationToken ct = default);

    /// <summary>
    /// Checks whether the blob is currently available in the local cache without network I/O.
    /// </summary>
    bool IsCachedLocally(BlobId blobId);

    /// <summary>
    /// Removes an expired or deleted blob from local disk cache.
    /// </summary>
    ValueTask DeleteLocalCacheAsync(BlobId blobId, CancellationToken ct = default);
}
```

---

## 4. Contract Invariants & Guarantees

1. **Payload Size Invariant**:
   - `BlobReference` serialized size is $< 500\text{ bytes}$, fitting easily within `MaxPayloadBytes` (64 KB).
2. **Cryptographic Redaction**:
   - `BlobReference.ToString()` must **always** redact `EncryptionKey` and `BaseNonce` as `[REDACTED]`, preventing sensitive symmetric keys from appearing in diagnostic logs.
3. **Client-Side Privacy Boundary**:
   - All geolocation, camera serial numbers, and device tags in EXIF/XMP must be sanitized before encryption.
   - Plaintext media must be bucket-padded prior to encryption to eliminate file-size fingerprinting.
4. **Topology-Driven Transmission**:
   - **1:1 Direct (No Relay)**: Online-only peer pull. `UploadBlobAsync` stages ciphertext in the local chunk store; the receiver pulls chunks over a multiplexed peer stream. Fails with `DIRECT_PEER_OFFLINE` if the remote peer is not connected.
   - **1:1 Relayed & GroupChannel**: Single-relay upload. Senders upload ciphertext once to the designated channel relay; recipients fetch independently.

---

## 5. Implementation Status

- [x] **`BlobId.cs`**: Implemented in `Percolator.PluginSdk`.
- [x] **`BlobStorageMode.cs`**: Implemented in `Percolator.PluginSdk`.
- [x] **`TransferProgress.cs`**: Implemented in `Percolator.PluginSdk`.
- [x] **`BlobReference.cs`**: Implemented in `Percolator.PluginSdk` with key redaction.
- [x] **`BlobTransferModels.cs`**: Implemented in `Percolator.PluginSdk`.
- [x] **`IBlobStorageService.cs`**: Implemented in `Percolator.PluginSdk`.
- [x] **`Application2` Service**: `BlobStorageService` implemented and verified by 5 unit tests in `Percolator.Application2.Tests` (62/62 passing tests).

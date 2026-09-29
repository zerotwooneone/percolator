# Percolator.Apps.FileTransfer Implementation Plan

## Summary & Architectural Constraints
- **Target Project**: `Percolator.Apps.FileTransfer` (Decentralized file sharing, torrent-like manifest distribution, and out-of-band data chunk streaming).
- **Architectural Rules (Rule 1 & Rule 2)**:
  - Depends **only** on `Percolator.Domain` and `Percolator.PluginSdk`.
  - Zero reference to infrastructure/transport/storage libraries (no raw TCP/UDP sockets, gRPC client code, SQLite, or OS disk APIs).
  - Implements `IAppPlugin` (`AppId.FileTransfer = 0x03`) and `IAppPayloadHandler` from `Percolator.PluginSdk`.
  - Strict separation of protocol control vs. out-of-band data streaming:
    - **In-Band Manifests & Queries**: Manifest advertisements, file availability queries, chunk requests, and transfer negotiations flow strictly as application payloads on top of the domain communication channels (`DirectChannel` / `GroupChannel`). They do **not** appear in chat message feeds.
    - **Out-of-Band Data Streaming**: Actual file binary transfers (e.g. 2GB payload data) do **not** travel through domain Double Ratchets or conversation message histories, and do not use the core messaging gRPC endpoints. High-bandwidth file chunk streaming is executed out-of-band via dedicated infrastructure transfer adapters (e.g., direct TCP/QUIC data streams or torrent-style swarm transports) using authentication tokens negotiated in-band.
  - Test-first implementation: All behaviors must have corresponding unit tests in `Percolator.Apps.FileTransfer.Tests` using in-memory test doubles.

---

## Milestone 1: File Transfer Plugin Architecture & Manifest DTOs

### 1.1 Plugin Definition & Manifest DTOs
- **`FileTransferPlugin`**: Implements `IAppPlugin` with `AppId = 0x03` and semantic versioning.
- **Application Payload DTOs** (Protobuf serialization abstracted via `IPayloadSerializer`):
  - `FileManifestDto`: File metadata, total byte length, chunk size (e.g. 64KB), Merkle root hash, list of chunk SHA-256 hashes, and transfer authorization token.
  - `ManifestQueryDto`: Request to query public or shared manifests available from a peer or channel.
  - `ManifestQueryResponseDto`: Response containing available file manifests.
  - `TransferNegotiationDto`: Handshake payload specifying out-of-band candidate endpoints (IP, port, protocol) and single-use session transfer token.
- **`FileTransferPayloadHandler`**:
  - Implements `IAppPayloadHandler` for `AppId.FileTransfer`.
  - Ingress processing: Deserializes inbound manifest packets and routes to the manifest catalog or transfer session coordinator.
  - Manifest messages are isolated from chat applications—they are handled solely by the file transfer subsystem.

### 1.2 Test Doubles & Unit Tests (`Percolator.Apps.FileTransfer.Tests/Ingress`)
- `FileTransferPayloadHandlerTests.HandleInboundAsync_FileManifest_RegistersInCatalog`: asserts received manifest registered.
- `FileTransferPayloadHandlerTests.HandleInboundAsync_ManifestQuery_ReturnsAvailableManifests`: asserts query response dispatching.
- `FileTransferPayloadHandlerTests.HandleInboundAsync_TransferNegotiation_EmitsHandshakeEvent`: asserts out-of-band negotiation initialization.

---

## Milestone 2: Manifest Catalog & Merkle Verification

### 2.1 Manifest Catalog Domain Model
- **`FileManifest` Entity**:
  - `ManifestId`, `Filename`, `FileSizeBytes`, `ChunkSizeBytes`, `MerkleRootHash`, `ChunkHashes`.
  - Method `VerifyChunk(int chunkIndex, ReadOnlySpan<byte> chunkData)`: Computes chunk hash and verifies against Merkle tree.
- **`IManifestCatalogRepository` Port**:
  - In-memory / persistent abstraction for indexing hosted and remote file manifests.

### 2.2 Test Doubles & Unit Tests (`Percolator.Apps.FileTransfer.Tests/Catalog`)
- `FileManifestTests.VerifyChunk_WithValidChunk_ReturnsSuccess`: verifies chunk data integrity.
- `FileManifestTests.VerifyChunk_WithTamperedData_ReturnsCorruptChunkError`: verifies Merkle leaf hash mismatch detection.
- `InMemoryManifestCatalogTests.RegisterAndQuery_MaintainsCatalogIntegrity`: test double validation.

---

## Milestone 3: Out-of-Band Transfer Coordination

### 3.1 Out-of-Band Transfer Protocol & Ports
- **`IOutBandTransferAdapter` Port**:
  - Outbound port defining out-of-band binary transfer coordination:
    - `Task<TransferSessionResult> StartOutBandTransferAsync(TransferToken token, EndpointCandidate endpoint, CancellationToken ct)`.
  - Concrete implementation lives in `Infrastructure2` (e.g. raw TCP, QUIC, or WebRTC data channel).
- **`TransferSessionCoordinator`**:
  - Coordinates active file downloads and uploads.
  - Tracks bitfield / chunk completion bitmap.
  - Emits progress events and reassembles completed files upon 100% chunk verification.

### 3.2 Test Doubles & Unit Tests (`Percolator.Apps.FileTransfer.Tests/Coordination`)
- `TransferSessionCoordinatorTests.OnChunkReceived_UpdatesBitfield`: asserts progress tracking.
- `TransferSessionCoordinatorTests.OnAllChunksCompleted_EmitsTransferCompletedEvent`: asserts file assembly completion.
- `TransferSessionCoordinatorTests.OnConnectionDropped_ResumesFromCurrentBitfield`: asserts resume capability.

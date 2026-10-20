# Percolator.Apps.FileTransfer: Application Implementation Plan

## Refinement Status Gate
- [x] Lens 1: Scope & Feature Slice Framing (Status: Approved)
- [x] Lens 2: Onion Architecture & Primitive Obsession (Status: Approved)
- [x] Lens 3: Threat Modeling & Security Posture (Status: Approved)
- [x] Lens 4: High-Value, Low-Brittle Unit Test Design (Status: Approved)
- [x] Lens 5: Cross-Layer Infrastructure Alignment (Status: Approved)
- [x] Implementation Gate: UNLOCKED (All Lenses Approved)

---

## 1. Summary, Plugin Lifecycle & Inherent Privacy Posture

- **Target Project**: `Percolator.Apps.FileTransfer` (`AppId = 0x03`).
- **Target Environment**: **Desktop only** (multi-core processors, high-bandwidth unmetered networks, fast SSD/NVMe storage, large addressable memory, sparse file OS support).
- **Core Inspiration**: **croc** (multi-stream parallel socket saturation, direct P2P with relay pipe fallback, directory tree bundling, chunked streaming, bitfield-based resumption) combined with **BitTorrent swarm piece sharing, peer tracking, and selective downloads**.
- **Dependencies**: Depends on `Percolator.Domain.Kernel`, `Percolator.Apps.FileTransfer.Domain`, `Percolator.PluginSdk`, and `Percolator.SourceGenerators` analyzer. **Zero reference to `Percolator.Domain` core messaging, concrete transport, or SQLite implementations**.

---

### 1.1 Required Codebase & Project Modifications

To enforce strict Onion Architecture and align with repository standards:

1. **Update `Percolator.Apps.FileTransfer.csproj`**:
   - **Remove**: `<ProjectReference Include="..\Percolator.Domain\Percolator.Domain.csproj" />`
   - **Add**: `<ProjectReference Include="..\Percolator.Apps.FileTransfer.Domain\Percolator.Apps.FileTransfer.Domain.csproj" />`
   - **Add**: `<ProjectReference Include="..\Percolator.SourceGenerators\Percolator.SourceGenerators.csproj" OutputItemType="Analyzer" ReferenceOutputAssembly="false" />`
   - **Retain**: `<ProjectReference Include="..\Percolator.PluginSdk\Percolator.PluginSdk.csproj" />`
   ```xml
   <Project Sdk="Microsoft.NET.Sdk">
     <PropertyGroup>
       <TargetFramework>net10.0</TargetFramework>
       <ImplicitUsings>enable</ImplicitUsings>
       <Nullable>enable</Nullable>
     </PropertyGroup>

     <ItemGroup>
       <ProjectReference Include="..\Percolator.Apps.FileTransfer.Domain\Percolator.Apps.FileTransfer.Domain.csproj" />
       <ProjectReference Include="..\Percolator.PluginSdk\Percolator.PluginSdk.csproj" />
       <ProjectReference Include="..\Percolator.SourceGenerators\Percolator.SourceGenerators.csproj" 
                         OutputItemType="Analyzer" 
                         ReferenceOutputAssembly="false" />
     </ItemGroup>
   </Project>
   ```

2. **Dependency Injection Wireup (`DependencyInjection/ServiceCollectionExtensions.cs`)**:
   - Implements standard extension method `AddFileTransferApp(this IServiceCollection collection)`:
     ```csharp
     namespace Percolator.Apps.FileTransfer.DependencyInjection;

     public static class ServiceCollectionExtensions
     {
         public static IServiceCollection AddFileTransferApp(this IServiceCollection collection)
         {
             collection.AddSingleton<IAppPlugin, FileTransferPlugin>();
             collection.AddSingleton<IAppIngressFilter, FileTransferIngressFilter>();
             collection.AddSingleton<IAppPayloadHandler, FileTransferPayloadHandler>();
             collection.AddScoped<IFileTransferActionHandler, FileTransferActionHandler>();
             collection.AddScoped<TransferSessionCoordinator>();
             collection.AddScoped<DiskChunkAssembler>();
             collection.AddScoped<ITransferOfferReaperService, TransferOfferReaperService>();

             return collection;
         }
     }
     ```

---

### 1.2 Project Directory & Source File Layout

```
Percolator.Apps.FileTransfer/
├── DependencyInjection/
│   └── ServiceCollectionExtensions.cs
├── Features/
│   ├── IngressControl/
│   │   ├── FileTransferFilterPolicy.cs
│   │   ├── FileTransferIngressFilter.cs
│   │   ├── FileTransferPayloadHandler.cs
│   │   └── IngressTransferOfferValidator.cs
│   ├── TransferNegotiation/
│   │   ├── ApplicationValueObjects.cs
│   │   ├── ControlPayloadOpcodes.cs
│   │   ├── FileTransferAcceptPayload.cs
│   │   ├── FileTransferCancelPayload.cs
│   │   ├── FileTransferOfferPayload.cs
│   │   └── FileTransferActionHandler.cs
│   ├── MultiStreamEngine/
│   │   ├── ChunkNonceDerivation.cs
│   │   ├── ChunkPipelineScheduler.cs
│   │   ├── StreamFrameEnvelope.cs
│   │   ├── StreamFramePayloads.cs
│   │   ├── StreamFrameType.cs
│   │   └── TransferSessionCoordinator.cs
│   ├── SwarmCoordination/
│   │   ├── EndgameCoordinator.cs
│   │   ├── PexRateLimiter.cs
│   │   ├── RarestFirstChunkSelector.cs
│   │   ├── SwarmCoordinator.cs
│   │   └── SwarmPeerLiveness.cs
│   └── Storage/
│       ├── DiskChunkAssembler.cs
│       └── SelectiveDownloadManager.cs
├── Ports/
│   ├── IDesktopFileStorage.cs
│   ├── IDistributedTrackerPort.cs
│   ├── IFileTransferActionHandler.cs
│   ├── IFileTransferFilterPolicyRepository.cs
│   ├── IFileTransferQueryService.cs
│   ├── ITransferOfferReaperService.cs
│   ├── ITransferRateLimiter.cs
│   ├── ITransferSessionRepository.cs
│   ├── ITransferStream.cs
│   ├── ITransferStreamPool.cs
│   ├── ITransferStreamPoolFactory.cs
│   ├── ManifestFileReadModel.cs
│   └── TransferSessionReadModel.cs
└── FileTransferPlugin.cs
```

---

### 1.3 Inherent Swarm & Three-Tier Privacy Ladder (Intentional Design)

Unlike Percolator's core Double Ratchet messaging—which enforces continuous per-message forward secrecy and shields IP addresses behind sovereign relay mailboxes—the FileTransfer app balances multi-stream throughput with a **strict three-tier privacy ladder**:

1. **Tier 1: Default WAN Mode (Blind Relay Pipes) — ZERO IP Exposure**:
   - By default (`DirectWanPolicy.DisabledByDefault`), all internet/WAN transfers route exclusively through blind relay pipes (`POST /transfer-pipes/{sessionId}`).
   - The relay operates as an oblivious memory-buffered socket splicer; it cannot see file contents ($K_{\text{transfer}}$ encryption) and peers **never learn each other's physical IP addresses, ISP locations, or NAT topologies**.
2. **Tier 2: Direct LAN Mode — Local Subnet IP Only**:
   - When peers are detected on the same local subnet (e.g., home or office network), direct sockets connect over private RFC 1918 addresses (`192.168.x.x`, `10.x.x.x`).
   - Only local intranet addresses are exchanged; physical public WAN IP addresses are never exposed to the wider internet.
3. **Tier 3: Direct WAN Swarm (Explicit User Opt-In Only) — Public IP Exposure**:
   - Enabled **strictly** when the user explicitly grants permission (`DirectWanPolicy.ExplicitlyAllowed`).
   - Direct socket saturation and multi-peer piece sharing establish direct internet TCP/QUIC connections across WAN UPnP or STUN endpoints, exposing public IPv4/IPv6 addresses **exclusively to invited contacts (`TargetRecipients`)**, never to public open swarms.
   - **UI Presentation Responsibility**: The Presentation layer (UI) bears the explicit responsibility of warning users before initiating or accepting direct WAN transfers, displaying the participating contacts (`PublicIdentityId`) whose IP addresses will be revealed.

#### Cryptographic & Activity Privacy Invariants:
1. **Static Symmetric Session Key ($K_{\text{transfer}}$)**: To eliminate re-ratcheting overhead on millions of continuous bulk chunks, all chunks in a transfer session are encrypted with a single static symmetric AES-256 key negotiated in-band over Double Ratchet. Forward secrecy and break-in recovery are intentionally bounded to that transfer session; compromise of this session key decrypts all chunks for that session.
2. **Chunk Possession & Activity Leakage**: BitTorrent swarming relies on high-frequency possession broadcasts (`HAVE` and `BITFIELD` frames). Connected swarm peers actively reveal which chunks they possess, which pieces they are requesting, and their real-time download progress to other participating peers.
3. **Deniable Symmetric Secrets**: Stream handshakes rely exclusively on ephemeral symmetric HMAC tokens derived from $K_{\text{transfer}}$. Zero root identity keys or digital signatures are transmitted over raw network sockets, guaranteeing cryptographic deniability and preventing any third-party proof binding a physical IP address to a user identity.
4. **Swarm Tracker Identity Exposure (Intentional Tradeoff)**: When using the out-of-band relay swarm tracker (`RelaySwarmTrackerClient`), peers announce direct socket candidates alongside their `PublicIdentityId` under the session's blinded rendezvous ID. The relay learns which invited identities are participating in the swarm and their physical IP addresses. This is an explicit, intentional architectural tradeoff to enable instant peer-to-endpoint attribution without complex pseudonym translation layers, bounded by strictly ephemeral 90-second in-memory sliding retention and proactive unregistration upon session completion.

---

## 2. Ingress Edge Pre-Filtering & Dispatching

```mermaid
flowchart TD
    InboundPacket["Inbound Packet (AppId: 0x03)"] --> IngressCheck{"IAppIngressFilter.IsEnabled?"}
    IngressCheck -->|No| DiscardSilent["Discard Immediately (Zero Memory, Zero Deserialization)"]
    IngressCheck -->|Yes| FilterCheck{"Sender or Relay Blocked in Policy?"}
    FilterCheck -->|Yes| DropSilent["Drop Silently (Zero Logging, Zero Wire ACK)"]
    FilterCheck -->|No| StageInvite["Stage Inbound Session (State: PendingConsent)"]
    StageInvite --> ZeroWire["SILENT ON WIRE: Zero ACK / Zero Response Sent"]
    ZeroWire --> ExposeReadModel["Expose TransferSessionReadModel to Ports"]
```

### 2.1 Plugin Lifecycle & Ingress Filter Implementations

```csharp
namespace Percolator.Apps.FileTransfer;

public sealed class FileTransferPlugin : IAppPlugin
{
    public AppId Id => AppId.FileTransferControl;
    public string Name => "FileTransfer";

    public Task StartAsync(CancellationToken ct = default) => Task.CompletedTask;
    public Task StopAsync(CancellationToken ct = default) => Task.CompletedTask;
}
```

```csharp
namespace Percolator.Apps.FileTransfer.Features.IngressControl;

public enum PeerFilterDecision : byte
{
    Allowed = 0x01,
    Blocked = 0x02
}

public enum RelayFilterDecision : byte
{
    Allowed = 0x01,
    Blocked = 0x02
}

public sealed record FileTransferFilterPolicy(
    PublicIdentityId OwnerId,
    IReadOnlySet<PublicIdentityId> BlockedPeers,
    IReadOnlySet<RelayPipeEndpointUri> BlockedRelays)
{
    public static FileTransferFilterPolicy DefaultPermissive(PublicIdentityId ownerId) =>
        new(ownerId, new HashSet<PublicIdentityId>(), new HashSet<RelayPipeEndpointUri>());

    public PeerFilterDecision EvaluatePeer(PublicIdentityId peerId) =>
        BlockedPeers.Contains(peerId) ? PeerFilterDecision.Blocked : PeerFilterDecision.Allowed;

    public RelayFilterDecision EvaluateRelay(RelayPipeEndpointUri relayUri) =>
        BlockedRelays.Contains(relayUri) ? RelayFilterDecision.Blocked : RelayFilterDecision.Allowed;
}

public sealed class FileTransferIngressFilter : IAppIngressFilter
{
    private readonly IFileTransferFilterPolicyRepository _policyRepository;

    public AppId TargetAppId => AppId.FileTransferControl;
    public bool IsEnabled { get; set; } = true;

    public FileTransferIngressFilter(IFileTransferFilterPolicyRepository policyRepository)
    {
        _policyRepository = policyRepository;
    }

    public IngressDecision Evaluate(InboundPayloadContext context)
    {
        if (!IsEnabled)
            return IngressDecision.DiscardSilently;

        // Synchronous fast check against cached/loaded policy
        FileTransferFilterPolicy policy = _policyRepository.GetPolicyAsync(context.SenderIdentityId).AsTask().GetAwaiter().GetResult();
        if (policy.EvaluatePeer(context.SenderIdentityId) == PeerFilterDecision.Blocked)
        {
            return IngressDecision.DiscardSilently;
        }

        return IngressDecision.Accept;
    }
}
```

---

### 2.2 In-Band Control Payload Demuxing & Handler Implementation

In-band payloads carried across Double Ratchet envelopes use a 1-byte opcode discriminator:

```csharp
namespace Percolator.Apps.FileTransfer.Features.TransferNegotiation;

public enum FileTransferControlOpcode : byte
{
    Offer = 0x01,
    Accept = 0x02,
    Cancel = 0x03
}
```

```csharp
namespace Percolator.Apps.FileTransfer.Features.IngressControl;

public sealed class FileTransferPayloadHandler : IAppPayloadHandler
{
    private readonly ITransferSessionRepository _sessionRepository;
    private readonly IFileTransferFilterPolicyRepository _policyRepository;
    private readonly IDateTimeProvider _clock;

    public AppId TargetAppId => AppId.FileTransferControl;

    public FileTransferPayloadHandler(
        ITransferSessionRepository sessionRepository,
        IFileTransferFilterPolicyRepository policyRepository,
        IDateTimeProvider clock)
    {
        _sessionRepository = sessionRepository;
        _policyRepository = policyRepository;
        _clock = clock;
    }

    public async ValueTask<DomainResult> HandleInboundAsync(InboundPayloadContext context, CancellationToken ct = default)
    {
        if (context.Payload.Length < 1)
            return DomainResult.Failure(new DomainError("Payload.Empty", "Inbound payload was empty."));

        byte opcodeByte = context.Payload.Span[0];
        ReadOnlyMemory<byte> body = context.Payload.Slice(1);

        FileTransferFilterPolicy policy = await _policyRepository.GetPolicyAsync(context.SenderIdentityId, ct);
        if (policy.EvaluatePeer(context.SenderIdentityId) == PeerFilterDecision.Blocked)
            return DomainResult.Success(); // Silent drop

        return opcodeByte switch
        {
            (byte)FileTransferControlOpcode.Offer => await HandleOfferAsync(context.SenderIdentityId, body, policy, ct),
            (byte)FileTransferControlOpcode.Accept => await HandleAcceptAsync(context.SenderIdentityId, body, ct),
            (byte)FileTransferControlOpcode.Cancel => await HandleCancelAsync(context.SenderIdentityId, body, ct),
            _ => DomainResult.Failure(new DomainError("Payload.UnknownOpcode", $"Unknown control opcode 0x{opcodeByte:X2}."))
        };
    }

    private async ValueTask<DomainResult> HandleOfferAsync(
        PublicIdentityId senderId,
        ReadOnlyMemory<byte> body,
        FileTransferFilterPolicy policy,
        CancellationToken ct)
    {
        FileTransferOfferPayload offer = FileTransferOfferPayload.Deserialize(body);

        ValidationResult validation = IngressTransferOfferValidator.ValidateOffer(offer);
        if (!validation.IsValid)
            return DomainResult.Success(); // Silent discard on malformed offer bounds

        if (offer.RelayRendezvous != null && policy.EvaluateRelay(offer.RelayRendezvous.EndpointUri) == RelayFilterDecision.Blocked)
            return DomainResult.Success(); // Silent drop on blocked relay

        // Build inline manifest if provided inline
        TransferManifest manifest;
        if (offer.Manifest.Kind == ManifestDistributionKind.Inline && offer.Manifest.InlineData != null)
        {
            manifest = TransferManifestDeserializer.Deserialize(offer.Manifest.InlineData.Span, offer.TransferSessionId);
        }
        else
        {
            // Staged manifest placeholder pending remote blob retrieval
            manifest = TransferManifest.CreatePlaceholder(offer.TransferSessionId, offer.Summary);
        }

        TransferSession session = TransferSession.CreateIncomingStaged(
            offer.TransferSessionId,
            senderId,
            manifest,
            SeedRatio.Zero,
            _clock);

        await _sessionRepository.SaveSessionAsync(session, ct);
        return DomainResult.Success(); // Zero wire ACK emitted
    }

    private async ValueTask<DomainResult> HandleAcceptAsync(
        PublicIdentityId senderId,
        ReadOnlyMemory<byte> body,
        CancellationToken ct)
    {
        FileTransferAcceptPayload accept = FileTransferAcceptPayload.Deserialize(body);

        TransferSession? session = await _sessionRepository.GetSessionByIdAsync(accept.TransferSessionId, ct);
        if (session == null)
            return DomainResult.Failure(new DomainError("Transfer.NotFound", "Transfer session was not found."));

        DomainResult result = session.RecordPeerAcceptance(senderId, accept.ResumptionBitfield, _clock);
        if (result.IsFailure)
            return result;

        await _sessionRepository.SaveSessionAsync(session, ct);
        return DomainResult.Success();
    }

    private async ValueTask<DomainResult> HandleCancelAsync(
        PublicIdentityId senderId,
        ReadOnlyMemory<byte> body,
        CancellationToken ct)
    {
        FileTransferCancelPayload cancel = FileTransferCancelPayload.Deserialize(body);

        TransferSession? session = await _sessionRepository.GetSessionByIdAsync(cancel.TransferSessionId, ct);
        if (session == null || !session.Topology.ContainsParticipant(senderId))
            return DomainResult.Success(); // Silent drop for unknown or unauthorized sender

        DomainResult result = session.Cancel(cancel.Reason, _clock);
        if (result.IsSuccess)
        {
            await _sessionRepository.SaveSessionAsync(session, ct);
        }

        return DomainResult.Success();
    }
}
```

---

### 2.3 Symmetric Bundle Limits Validator

```csharp
namespace Percolator.Apps.FileTransfer.Features.IngressControl;

public readonly record struct ValidationResult(bool IsValid, string? ErrorMessage)
{
    public static ValidationResult Success() => new(true, null);
    public static ValidationResult Failure(string error) => new(false, error);
}

public static class IngressTransferOfferValidator
{
    public const int MaxFilesPerBundle = 50_000;
    public const int MaxChunksPerBundle = 1_000_000;
    public const long MaxBundleSizeBytes = 16_000_000_000_000L; // 16 TB
    public const int MaxInlineManifestBytes = 49_152; // 48 KB (preserves headroom in 64 KB Double Ratchet envelope)
    public const long MaxRemoteManifestBlobBytes = 8_388_608L; // 8 MB

    public static ValidationResult ValidateOffer(FileTransferOfferPayload offer)
    {
        if (offer.Summary.TotalFiles.Count > MaxFilesPerBundle)
            return ValidationResult.Failure($"Offer file count ({offer.Summary.TotalFiles.Count}) exceeds maximum allowed ({MaxFilesPerBundle}).");
        if (offer.Summary.TotalChunks.Count > MaxChunksPerBundle)
            return ValidationResult.Failure($"Offer chunk count ({offer.Summary.TotalChunks.Count}) exceeds maximum allowed ({MaxChunksPerBundle}).");
        if (offer.Summary.TotalSize.Bytes > MaxBundleSizeBytes)
            return ValidationResult.Failure($"Offer bundle size ({offer.Summary.TotalSize.Bytes} bytes) exceeds maximum allowed ({MaxBundleSizeBytes} bytes).");
        if (offer.Manifest.Kind == ManifestDistributionKind.Inline)
        {
            if (offer.Manifest.InlineData == null || offer.Manifest.InlineData.Length > MaxInlineManifestBytes)
                return ValidationResult.Failure($"Inline manifest exceeds maximum allowed limit ({MaxInlineManifestBytes} bytes).");
        }
        else if (offer.Manifest.Kind == ManifestDistributionKind.RemoteBlob)
        {
            if (offer.Manifest.BlobDescriptor == null ||
                offer.Manifest.BlobDescriptor.PlaintextSizeBytes.Bytes <= 0 ||
                offer.Manifest.BlobDescriptor.PlaintextSizeBytes.Bytes > MaxRemoteManifestBlobBytes)
                return ValidationResult.Failure($"Remote manifest blob size ({offer.Manifest.BlobDescriptor?.PlaintextSizeBytes.Bytes ?? 0} bytes) must be between 1 and {MaxRemoteManifestBlobBytes} bytes.");
        }
        return ValidationResult.Success();
    }
}
```

---

## 3. Application Wire Payloads & Out-of-Band Framing

### 3.1 Application Value Objects & Transport Models

> [!IMPORTANT]
> **Strict Prohibition of Implicit / Explicit Conversion Operators**:
> In alignment with `FileTransfer.Domain`, all Application Value Objects, transport models, cryptographic secrets, and filesystem paths enforce a strict **zero implicit/explicit conversion operator** policy.

```csharp
namespace Percolator.Apps.FileTransfer.Features.TransferNegotiation;

public enum DirectCandidateKind : byte
{
    DirectLan = 0x01,
    DirectWanUpnp = 0x02,
    DirectWanStun = 0x03
}

public enum DirectWanPolicy : byte
{
    DisabledByDefault = 0x01,
    ExplicitlyAllowed = 0x02
}

public enum TransportRoutingPreference : byte
{
    DirectPreferred = 0x01,
    RelayOnly = 0x02
}

public sealed record EndpointHost
{
    public string Value { get; }
    private EndpointHost(string value) => Value = value;
    public static EndpointHost Parse(string host)
    {
        if (string.IsNullOrWhiteSpace(host)) throw new ArgumentException("Host cannot be empty.", nameof(host));
        return new EndpointHost(host);
    }
}

public readonly record struct PortNumber
{
    public ushort Value { get; }
    private PortNumber(ushort value) => Value = value;
    public static PortNumber FromInt(int port)
    {
        if (port is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(port), "Port must be 1..65535.");
        return new PortNumber((ushort)port);
    }
}

public sealed record DirectEndpointCandidate(
    EndpointHost Host,
    PortNumber Port,
    DirectCandidateKind Kind);

public sealed record RelayPipeEndpointUri
{
    public string Value { get; }
    private RelayPipeEndpointUri(string value) => Value = value;
    public static RelayPipeEndpointUri Parse(string uri) => new(uri ?? throw new ArgumentNullException(nameof(uri)));
}

public sealed record RelayPipeToken([property: ByteArray(32)] byte[] Bytes);

public sealed record RelayRendezvousDescriptor(
    RelayPipeEndpointUri EndpointUri,
    RelayPipeToken Token);

public sealed record TransferKey([property: ByteArray(32)] byte[] Bytes) : ISensitiveSecret
{
    public static TransferKey Generate() => new(RandomNumberGenerator.GetBytes(32));
    public void Zeroize() => CryptographicOperations.ZeroMemory(Bytes);
}

public sealed record BaseNonce([property: ByteArray(12)] byte[] Bytes)
{
    public static BaseNonce Generate() => new(RandomNumberGenerator.GetBytes(12));
}

public sealed record StreamAuthToken([property: ByteArray(32)] byte[] Bytes)
{
    public static StreamAuthToken Generate() => new(RandomNumberGenerator.GetBytes(32));
}

public sealed record SwarmRendezvousId([property: ByteArray(32)] byte[] Bytes)
{
    public static SwarmRendezvousId Derive(TransferKey key)
    {
        byte[] derived = HMACSHA256.HashData(key.Bytes, System.Text.Encoding.UTF8.GetBytes("swarm-rendezvous"));
        return new SwarmRendezvousId(derived);
    }
}

public readonly record struct StreamIndex(int Value)
{
    public static StreamIndex FromInt(int index) => new(index);
}

public readonly record struct StreamCount(int Count)
{
    public static StreamCount FromInt(int count) => new(count);
    public static StreamCount Default => new(4);
}

public readonly record struct TransferBandwidth(long BytesPerSecond);

public readonly record struct BandwidthLimit(bool IsUnlimited, TransferBandwidth Value)
{
    public static BandwidthLimit Unlimited => new(true, default);
    public static BandwidthLimit Capped(TransferBandwidth cap) => new(false, cap);
}

public sealed record RootDirectoryPath
{
    public string Value { get; }
    private RootDirectoryPath(string value) => Value = value;
    public static RootDirectoryPath Parse(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("Path cannot be empty.", nameof(path));
        return new RootDirectoryPath(Path.GetFullPath(path));
    }
}

public sealed record ResolvedFilePath
{
    public string AbsolutePath { get; }
    private ResolvedFilePath(string absolutePath) => AbsolutePath = absolutePath;
    public static ResolvedFilePath Resolve(RootDirectoryPath root, SanitizedRelativePath relativePath) =>
        new(Path.Combine(root.Value, relativePath.Value));
}

public sealed record EncryptedChunkData
{
    public ReadOnlyMemory<byte> CiphertextWithAuthTag { get; }
    public int Length => CiphertextWithAuthTag.Length;
    public ReadOnlySpan<byte> Span => CiphertextWithAuthTag.Span;

    private EncryptedChunkData(ReadOnlyMemory<byte> data) => CiphertextWithAuthTag = data;
    public static EncryptedChunkData FromBytes(byte[] data) => new(data ?? throw new ArgumentNullException(nameof(data)));
    public static EncryptedChunkData FromMemory(ReadOnlyMemory<byte> memory) => new(memory);
}

public sealed record InlineManifestData
{
    public ReadOnlyMemory<byte> SerializedBytes { get; }
    public int Length => SerializedBytes.Length;
    public ReadOnlySpan<byte> Span => SerializedBytes.Span;

    private InlineManifestData(ReadOnlyMemory<byte> bytes) => SerializedBytes = bytes;
    public static InlineManifestData FromBytes(byte[] bytes)
    {
        if (bytes == null || bytes.Length == 0 || bytes.Length > 49_152)
            throw new ArgumentException("Inline manifest must be between 1 and 49152 bytes.", nameof(bytes));
        return new InlineManifestData(bytes);
    }
}

public sealed record ManifestBlobDescriptor(
    BlobId BlobId,
    ChunkHash CiphertextSha256,
    TotalBundleBytes PlaintextSizeBytes,
    RelayPipeEndpointUri RelayEndpointUri);

public enum ManifestDistributionKind : byte
{
    Inline = 0x01,
    RemoteBlob = 0x02
}

public sealed record ManifestDistribution
{
    public ManifestDistributionKind Kind { get; }
    public InlineManifestData? InlineData { get; }
    public ManifestBlobDescriptor? BlobDescriptor { get; }

    private ManifestDistribution(ManifestDistributionKind kind, InlineManifestData? inlineData, ManifestBlobDescriptor? blob)
    {
        Kind = kind;
        InlineData = inlineData;
        BlobDescriptor = blob;
    }

    public static ManifestDistribution FromInline(InlineManifestData data) =>
        new(ManifestDistributionKind.Inline, data ?? throw new ArgumentNullException(nameof(data)), null);

    public static ManifestDistribution FromBlob(ManifestBlobDescriptor blob) =>
        new(ManifestDistributionKind.RemoteBlob, null, blob ?? throw new ArgumentNullException(nameof(blob)));
}

public sealed record DiskSpacePreflightResult(
    bool HasSufficientSpace,
    TotalBundleBytes RequiredBytes,
    TotalBundleBytes AvailableBytes)
{
    public static DiskSpacePreflightResult Sufficient(TotalBundleBytes required, TotalBundleBytes available) =>
        new(true, required, available);
    public static DiskSpacePreflightResult Insufficient(TotalBundleBytes required, TotalBundleBytes available) =>
        new(false, required, available);
}
```

---

### 3.2 In-Band Control Payloads

```csharp
namespace Percolator.Apps.FileTransfer.Features.TransferNegotiation;

public sealed record FileTransferOfferPayload(
    TransferSessionId TransferSessionId,
    TransferBundleSummary Summary,
    ManifestDistribution Manifest,
    TransferKey SessionEncryptionKey,
    BaseNonce BaseNonce,
    StreamAuthToken StreamAuthToken,
    RelayRendezvousDescriptor? RelayRendezvous,
    IReadOnlyList<DirectEndpointCandidate> DirectCandidates)
{
    public static FileTransferOfferPayload Deserialize(ReadOnlyMemory<byte> bytes) =>
        FileTransferPayloadSerializer.DeserializeOffer(bytes.Span);

    public byte[] Serialize() =>
        FileTransferPayloadSerializer.SerializeOffer(this);
}

public sealed record FileTransferAcceptPayload(
    TransferSessionId TransferSessionId,
    IReadOnlyList<DirectEndpointCandidate> ReceiverCandidates,
    TransportRoutingPreference RoutingPreference,
    Bitfield ResumptionBitfield)
{
    public static FileTransferAcceptPayload Deserialize(ReadOnlyMemory<byte> bytes) =>
        FileTransferPayloadSerializer.DeserializeAccept(bytes.Span);

    public byte[] Serialize() =>
        FileTransferPayloadSerializer.SerializeAccept(this);
}

public sealed record FileTransferCancelPayload(
    TransferSessionId TransferSessionId,
    TransferTerminationReason Reason)
{
    public static FileTransferCancelPayload Deserialize(ReadOnlyMemory<byte> bytes) =>
        FileTransferPayloadSerializer.DeserializeCancel(bytes.Span);

    public byte[] Serialize() =>
        FileTransferPayloadSerializer.SerializeCancel(this);
}
```

---

### 3.3 Out-of-Band Stream Frame Envelopes & AES-256-GCM Framing

Out-of-band TCP/QUIC streams use a standard framing header:
- **Frame Header**: `[1-byte StreamFrameType] [4-byte BigEndian PayloadLength]` (5 bytes total)
- **Frame Payload**: `[PayloadLength bytes]`

```csharp
namespace Percolator.Apps.FileTransfer.Features.MultiStreamEngine;

public enum StreamFrameType : byte
{
    Handshake = 0x01,
    Bitfield = 0x02,
    Have = 0x03,
    RequestChunk = 0x04,
    ChunkData = 0x05,
    CancelChunk = 0x06,
    Choke = 0x07,
    Unchoke = 0x08,
    Interested = 0x09,
    NotInterested = 0x0A,
    KeepAlive = 0x0B,
    PexPeers = 0x0C
}

public interface IStreamFramePayload
{
    StreamFrameType FrameType { get; }
}

public sealed record HandshakeFrame(
    StreamAuthToken AuthToken,
    TransferSessionId SessionId,
    StreamIndex StreamIndex,
    PublicIdentityId SenderPeerId) : IStreamFramePayload
{
    public StreamFrameType FrameType => StreamFrameType.Handshake;
}

public sealed record BitfieldFrame(Bitfield Bitfield) : IStreamFramePayload
{
    public StreamFrameType FrameType => StreamFrameType.Bitfield;
}

public sealed record HaveFrame(ChunkIndex ChunkIndex) : IStreamFramePayload
{
    public StreamFrameType FrameType => StreamFrameType.Have;
}

public sealed record RequestChunkFrame(ChunkIndex ChunkIndex) : IStreamFramePayload
{
    public StreamFrameType FrameType => StreamFrameType.RequestChunk;
}

public sealed record ChunkDataFrame(ChunkIndex ChunkIndex, EncryptedChunkData Data) : IStreamFramePayload
{
    public StreamFrameType FrameType => StreamFrameType.ChunkData;
}

public sealed record CancelChunkFrame(ChunkIndex ChunkIndex) : IStreamFramePayload
{
    public StreamFrameType FrameType => StreamFrameType.CancelChunk;
}

public sealed record ChokeFrame : IStreamFramePayload
{
    public StreamFrameType FrameType => StreamFrameType.Choke;
}

public sealed record UnchokeFrame : IStreamFramePayload
{
    public StreamFrameType FrameType => StreamFrameType.Unchoke;
}

public sealed record InterestedFrame : IStreamFramePayload
{
    public StreamFrameType FrameType => StreamFrameType.Interested;
}

public sealed record NotInterestedFrame : IStreamFramePayload
{
    public StreamFrameType FrameType => StreamFrameType.NotInterested;
}

public sealed record KeepAliveFrame : IStreamFramePayload
{
    public StreamFrameType FrameType => StreamFrameType.KeepAlive;
}

public sealed record PexPeersFrame(IReadOnlyList<DirectEndpointCandidate> Candidates) : IStreamFramePayload
{
    public StreamFrameType FrameType => StreamFrameType.PexPeers;
}

public sealed record StreamFrameEnvelope(IStreamFramePayload Payload);
```

#### Deterministic Nonce Derivation & Cryptographic Routines

```csharp
namespace Percolator.Apps.FileTransfer.Features.MultiStreamEngine;

public sealed record ChunkNonce([property: ByteArray(12)] byte[] Bytes)
{
    public ReadOnlySpan<byte> Span => Bytes;

    public static ChunkNonce Derive(BaseNonce baseNonce, ChunkIndex chunkIndex)
    {
        byte[] derived = new byte[12];
        baseNonce.Bytes.CopyTo(derived, 0);

        int currentBigEndian = BinaryPrimitives.ReadInt32BigEndian(derived.AsSpan(8, 4));
        BinaryPrimitives.WriteInt32BigEndian(derived.AsSpan(8, 4), currentBigEndian ^ chunkIndex.Value);

        return new ChunkNonce(derived);
    }
}

public static class ChunkCryptoService
{
    public static EncryptedChunkData EncryptChunk(
        PlaintextChunkData plaintext,
        TransferKey key,
        BaseNonce baseNonce,
        ChunkIndex index)
    {
        ChunkNonce nonce = ChunkNonce.Derive(baseNonce, index);
        byte[] ciphertext = new byte[plaintext.Length];
        byte[] tag = new byte[16];

        using AesGcm gcm = new(key.Bytes, 16);
        gcm.Encrypt(nonce.Span, plaintext.Span, ciphertext, tag);

        byte[] combined = new byte[ciphertext.Length + 16];
        ciphertext.CopyTo(combined, 0);
        tag.CopyTo(combined, ciphertext.Length);

        return EncryptedChunkData.FromBytes(combined);
    }

    public static PlaintextChunkData DecryptChunk(
        EncryptedChunkData encrypted,
        TransferKey key,
        BaseNonce baseNonce,
        ChunkIndex index)
    {
        if (encrypted.Length < 16)
            throw new CryptographicException("Encrypted chunk data is too short to contain authentication tag.");

        ChunkNonce nonce = ChunkNonce.Derive(baseNonce, index);
        int cipherLength = encrypted.Length - 16;
        ReadOnlySpan<byte> ciphertext = encrypted.Span[..cipherLength];
        ReadOnlySpan<byte> tag = encrypted.Span[cipherLength..];

        byte[] plaintext = new byte[cipherLength];
        using AesGcm gcm = new(key.Bytes, 16);
        gcm.Decrypt(nonce.Span, ciphertext, tag, plaintext);

        return PlaintextChunkData.FromBytes(plaintext);
    }
}
```

---

## 4. Outbound Ports & Infrastructure Contracts

```csharp
namespace Percolator.Apps.FileTransfer.Ports;

public interface ITransferStream : IAsyncDisposable
{
    StreamIndex Index { get; }
    ValueTask SendFrameAsync(IStreamFramePayload frame, CancellationToken ct = default);
    ValueTask<IStreamFramePayload> ReceiveFrameAsync(CancellationToken ct = default);
}

public interface ITransferStreamPool : IAsyncDisposable
{
    StreamCount ActiveStreams { get; }
    ValueTask ConnectDirectAsync(IReadOnlyList<DirectEndpointCandidate> candidates, CancellationToken ct = default);
    ValueTask ConnectRelayPipeAsync(RelayRendezvousDescriptor rendezvous, CancellationToken ct = default);
    ITransferStream GetStream(StreamIndex index);
    ValueTask BroadcastFrameAsync(IStreamFramePayload frame, CancellationToken ct = default);
}

public interface ITransferStreamPoolFactory
{
    ITransferStreamPool CreatePool(TransferSessionId sessionId, PublicIdentityId peerId, StreamCount streamCount);
    ITransferStreamPool CreatePool(TransferSessionId sessionId, PublicIdentityId peerId);
}

public interface IDistributedTrackerPort
{
    ValueTask<IReadOnlyList<DirectEndpointCandidate>> AnnounceAsync(
        SwarmRendezvousId rendezvousId,
        PublicIdentityId memberId,
        IReadOnlyList<DirectEndpointCandidate> candidates,
        SwarmPeerRole role,
        CancellationToken ct = default);

    ValueTask UnregisterAsync(
        SwarmRendezvousId rendezvousId,
        PublicIdentityId memberId,
        CancellationToken ct = default);
}

public interface IDesktopFileStorage
{
    ValueTask PreAllocateFilesAsync(RootDirectoryPath rootPath, TransferManifest manifest, SelectedFileIndices selectedIndices, CancellationToken ct = default);
    ValueTask WriteChunkAsync(ResolvedFilePath targetPath, FileByteOffset fileOffset, PlaintextChunkData data, CancellationToken ct = default);
    ValueTask<PlaintextChunkData> ReadChunkAsync(ResolvedFilePath sourcePath, FileByteOffset fileOffset, TransferByteLength length, CancellationToken ct = default);
    ValueTask<Bitfield> ScanAndComputeBitfieldAsync(RootDirectoryPath rootPath, TransferManifest manifest, SelectedFileIndices? selectedIndices = null, CancellationToken ct = default);
    ValueTask FinalizeFileAsync(ResolvedFilePath targetPath, FileLastModifiedUtc lastModifiedUtc, CancellationToken ct = default);
    ValueTask<DiskSpacePreflightResult> CheckDiskSpaceAsync(RootDirectoryPath rootPath, TotalBundleBytes requiredBytes, CancellationToken ct = default);
}

public interface ITransferRateLimiter
{
    ValueTask SetGlobalSpeedCapsAsync(BandwidthLimit downloadCap, BandwidthLimit uploadCap, CancellationToken ct = default);
    ValueTask SetSessionSpeedCapsAsync(TransferSessionId sessionId, BandwidthLimit downloadCap, BandwidthLimit uploadCap, CancellationToken ct = default);
    ValueTask ThrottleDownloadAsync(TransferSessionId sessionId, TransferByteLength byteCount, CancellationToken ct = default);
    ValueTask ThrottleUploadAsync(TransferSessionId sessionId, TransferByteLength byteCount, CancellationToken ct = default);
}

public interface ITransferSessionRepository
{
    ValueTask SaveSessionAsync(TransferSession session, CancellationToken ct = default);
    ValueTask<TransferSession?> GetSessionByIdAsync(TransferSessionId sessionId, CancellationToken ct = default);
    ValueTask<IReadOnlyList<TransferSession>> GetActiveSessionsAsync(CancellationToken ct = default);
}

public interface IFileTransferFilterPolicyRepository
{
    ValueTask SavePolicyAsync(FileTransferFilterPolicy policy, CancellationToken ct = default);
    ValueTask<FileTransferFilterPolicy> GetPolicyAsync(PublicIdentityId ownerId, CancellationToken ct = default);
}
```

---

## 5. Minimal, Light-Touch CQRS Contracts & Concrete Action Services

### 5.1 `IFileTransferActionHandler` Contract

```csharp
namespace Percolator.Apps.FileTransfer.Ports;

public interface IFileTransferActionHandler
{
    ValueTask<DomainResult<TransferSessionId>> InitiateTransferAsync(
        IReadOnlyList<PublicIdentityId> recipients,
        RootDirectoryPath sourceRoot,
        IReadOnlyList<SanitizedRelativePath> filePaths,
        DirectWanPolicy wanPolicy,
        RootBundleName? customBundleName = null,
        SeedRatio? targetSeedRatio = null,
        CancellationToken ct = default);

    ValueTask<DomainResult> AcceptTransferAsync(
        TransferSessionId sessionId,
        RootDirectoryPath destinationFolder,
        SelectedFileIndices? selectedFiles = null,
        CancellationToken ct = default);

    ValueTask<DomainResult> PauseTransferAsync(TransferSessionId sessionId, CancellationToken ct = default);
    ValueTask<DomainResult> ResumeTransferAsync(TransferSessionId sessionId, CancellationToken ct = default);
    ValueTask<DomainResult> CancelTransferAsync(TransferSessionId sessionId, TransferTerminationReason reason, CancellationToken ct = default);
}
```

---

### 5.2 Concrete `FileTransferActionHandler` Orchestration

```csharp
namespace Percolator.Apps.FileTransfer.Features.TransferNegotiation;

public sealed class FileTransferActionHandler : IFileTransferActionHandler
{
    private readonly ITransferSessionRepository _sessionRepository;
    private readonly IDesktopFileStorage _fileStorage;
    private readonly IPayloadSender _payloadSender;
    private readonly IDistributedTrackerPort _trackerPort;
    private readonly IDateTimeProvider _clock;

    public FileTransferActionHandler(
        ITransferSessionRepository sessionRepository,
        IDesktopFileStorage fileStorage,
        IPayloadSender payloadSender,
        IDistributedTrackerPort trackerPort,
        IDateTimeProvider clock)
    {
        _sessionRepository = sessionRepository;
        _fileStorage = fileStorage;
        _payloadSender = payloadSender;
        _trackerPort = trackerPort;
        _clock = clock;
    }

    public async ValueTask<DomainResult<TransferSessionId>> InitiateTransferAsync(
        IReadOnlyList<PublicIdentityId> recipients,
        RootDirectoryPath sourceRoot,
        IReadOnlyList<SanitizedRelativePath> filePaths,
        DirectWanPolicy wanPolicy,
        RootBundleName? customBundleName = null,
        SeedRatio? targetSeedRatio = null,
        CancellationToken ct = default)
    {
        if (recipients == null || recipients.Count == 0)
            return DomainResult<TransferSessionId>.Failure(new DomainError("Transfer.NoRecipients", "Must specify at least one recipient."));

        if (filePaths == null || filePaths.Count == 0)
            return DomainResult<TransferSessionId>.Failure(new DomainError("Transfer.NoFiles", "Must specify at least one file to transfer."));

        if (filePaths.Count > IngressTransferOfferValidator.MaxFilesPerBundle)
            return DomainResult<TransferSessionId>.Failure(new DomainError("Transfer.FileLimitExceeded", $"File count ({filePaths.Count}) exceeds maximum allowed ({IngressTransferOfferValidator.MaxFilesPerBundle})."));

        // Scan files and build contiguous FileEntries
        List<FileEntry> entries = [];
        long cumulativeOffset = 0;
        for (int i = 0; i < filePaths.Count; i++)
        {
            SanitizedRelativePath relPath = filePaths[i];
            ResolvedFilePath absPath = ResolvedFilePath.Resolve(sourceRoot, relPath);
            FileInfo info = new(absPath.AbsolutePath);
            if (!info.Exists)
                return DomainResult<TransferSessionId>.Failure(new DomainError("Transfer.FileNotFound", $"File not found: '{relPath.Value}'."));

            FileSizeBytes size = FileSizeBytes.FromBytes(info.Length);
            BundleByteOffset startOffset = BundleByteOffset.FromOffset(cumulativeOffset);
            FileLastModifiedUtc lastModified = FileLastModifiedUtc.FromDateTimeOffset(info.LastWriteTimeUtc);

            entries.Add(new FileEntry(FileIndex.FromInt(i), relPath, size, startOffset, lastModified));
            cumulativeOffset += size.Bytes;
        }

        if (cumulativeOffset > IngressTransferOfferValidator.MaxBundleSizeBytes)
            return DomainResult<TransferSessionId>.Failure(new DomainError("Transfer.SizeLimitExceeded", "Bundle total size exceeds 16 TB."));

        ChunkSizeBytes chunkSize = ChunkSizeBytes.Default;
        int chunkCount = (int)Math.Max(1, (cumulativeOffset + chunkSize.Bytes - 1) / chunkSize.Bytes);
        if (chunkCount > IngressTransferOfferValidator.MaxChunksPerBundle)
            return DomainResult<TransferSessionId>.Failure(new DomainError("Transfer.ChunkLimitExceeded", "Bundle total chunk count exceeds 1,000,000."));

        // Build leaf hashes and Merkle tree
        List<ChunkHash> leafHashes = new(chunkCount);
        for (int c = 0; c < chunkCount; c++)
        {
            ChunkIndex chunkIdx = ChunkIndex.FromInt(c);
            ChunkByteLength expectedSize = ChunkByteLength.FromBytes((int)Math.Min((long)chunkSize.Bytes, cumulativeOffset - ((long)c * chunkSize.Bytes)));
            
            // Read bytes across files for this chunk and compute leaf hash
            byte[] chunkBuffer = new byte[expectedSize.Bytes];
            IReadOnlyList<FileChunkSlice> slices = ChunkBoundaryCalculator.CalculateSlicesFromEntries(entries, chunkSize, chunkIdx, expectedSize);
            foreach (FileChunkSlice slice in slices)
            {
                FileEntry entry = entries[slice.FileIndex.Value];
                ResolvedFilePath filePath = ResolvedFilePath.Resolve(sourceRoot, entry.RelativePath);
                PlaintextChunkData sliceData = await _fileStorage.ReadChunkAsync(filePath, slice.OffsetInFile, slice.SliceLength, ct);
                sliceData.Span.CopyTo(chunkBuffer.AsSpan(slice.BufferOffset, slice.SliceLength.Bytes));
            }

            leafHashes.Add(MerkleTreeBuilder.HashLeaf(chunkBuffer));
        }

        MerkleTreeIndex merkleTree = MerkleTreeBuilder.BuildFromLeaves(leafHashes);
        TransferSessionId sessionId = TransferSessionId.NewUnique();
        RootBundleName bundleTitle = customBundleName ?? RootBundleName.Parse(Path.GetFileName(sourceRoot.Value));
        TransferManifest manifest = TransferManifest.Create(sessionId, bundleTitle, chunkSize, merkleTree, entries);

        TransferKey sessionKey = TransferKey.Generate();
        BaseNonce baseNonce = BaseNonce.Generate();
        StreamAuthToken streamAuthToken = StreamAuthToken.Generate();
        SeedRatio enforcedSeedRatio = targetSeedRatio ?? SeedRatio.Zero;

        // Create domain aggregate
        PublicIdentityId localInitiator = recipients[0]; // Bound to local identity
        TransferSession session = recipients.Count == 1
            ? TransferSession.CreateOutgoing1to1(sessionId, localInitiator, recipients[0], manifest, enforcedSeedRatio, _clock)
            : TransferSession.CreateOutgoingMultiPeer(sessionId, localInitiator, recipients, manifest, enforcedSeedRatio, _clock);

        await _sessionRepository.SaveSessionAsync(session, ct);

        // Gather candidates respecting WAN policy
        List<DirectEndpointCandidate> candidates = [];
        if (wanPolicy == DirectWanPolicy.ExplicitlyAllowed)
        {
            // Resolve WAN STUN/UPnP candidates via discovery hooks
        }

        // Serialize inline or blob manifest
        byte[] serializedManifest = manifest.Serialize();
        ManifestDistribution manifestDist = serializedManifest.Length <= IngressTransferOfferValidator.MaxInlineManifestBytes
            ? ManifestDistribution.FromInline(InlineManifestData.FromBytes(serializedManifest))
            : ManifestDistribution.FromBlob(new ManifestBlobDescriptor(BlobId.NewUnique(), ChunkHash.FromBytes(SHA256.HashData(serializedManifest)), manifest.TotalSize, RelayPipeEndpointUri.Parse("https://relay.percolator.net/pipes")));

        FileTransferOfferPayload offerPayload = new(
            sessionId,
            TransferBundleSummary.Create(bundleTitle, manifest.TotalSize, manifest.TotalFiles, chunkSize, manifest.TotalChunks, merkleTree.Root),
            manifestDist,
            sessionKey,
            baseNonce,
            streamAuthToken,
            RelayRendezvous: null,
            candidates);

        byte[] inBandBytes = new byte[1 + offerPayload.Serialize().Length];
        inBandBytes[0] = (byte)FileTransferControlOpcode.Offer;
        offerPayload.Serialize().CopyTo(inBandBytes.AsSpan(1));

        await _payloadSender.SendPayloadAsync(new OutboundPayloadContext(sessionId.Value, AppId.FileTransferControl, inBandBytes), ct);

        return DomainResult<TransferSessionId>.Success(sessionId);
    }

    public async ValueTask<DomainResult> AcceptTransferAsync(
        TransferSessionId sessionId,
        RootDirectoryPath destinationFolder,
        SelectedFileIndices? selectedFiles = null,
        CancellationToken ct = default)
    {
        TransferSession? session = await _sessionRepository.GetSessionByIdAsync(sessionId, ct);
        if (session == null)
            return DomainResult.Failure(new DomainError("Transfer.NotFound", "Transfer session was not found."));

        if (session.State != TransferSessionState.PendingConsent)
            return DomainResult.Failure(new DomainError("Transfer.InvalidState", $"Cannot accept transfer in state '{session.State}'."));

        SelectedFileIndices chosenFiles = selectedFiles ?? SelectedFileIndices.All(session.Manifest.TotalFiles);
        if (chosenFiles.SelectedCount == 0)
            return DomainResult.Failure(new DomainError("Transfer.EmptySelection", "At least one file must be selected."));

        // Selective Disk Space Pre-flight Check
        long requiredBytes = 0;
        foreach (FileIndex idx in chosenFiles.Indices)
        {
            requiredBytes += session.Manifest.Files[idx.Value].FileSize.Bytes;
        }

        DiskSpacePreflightResult diskCheck = await _fileStorage.CheckDiskSpaceAsync(
            destinationFolder,
            TotalBundleBytes.FromBytes(requiredBytes),
            ct);

        if (!diskCheck.HasSufficientSpace)
            return DomainResult.Failure(new DomainError("Storage.InsufficientSpace", $"Insufficient disk space. Required: {requiredBytes} bytes."));

        DomainResult acceptResult = session.AcceptTransfer(chosenFiles, _clock);
        if (acceptResult.IsFailure)
            return acceptResult;

        // Immediate Finalization for Zero-Byte Files
        foreach (FileIndex idx in chosenFiles.Indices)
        {
            FileEntry file = session.Manifest.Files[idx.Value];
            if (file.FileSize.Bytes == 0)
            {
                ResolvedFilePath path = ResolvedFilePath.Resolve(destinationFolder, file.RelativePath);
                await _fileStorage.FinalizeFileAsync(path, file.LastModifiedUtc, ct);
            }
        }

        // Pre-allocate remaining non-empty files
        await _fileStorage.PreAllocateFilesAsync(destinationFolder, session.Manifest, chosenFiles, ct);
        await _sessionRepository.SaveSessionAsync(session, ct);

        // Send in-band accept payload
        FileTransferAcceptPayload acceptPayload = new(
            sessionId,
            ReceiverCandidates: [],
            TransportRoutingPreference.DirectPreferred,
            session.LocalBitfield);

        byte[] inBandBytes = new byte[1 + acceptPayload.Serialize().Length];
        inBandBytes[0] = (byte)FileTransferControlOpcode.Accept;
        acceptPayload.Serialize().CopyTo(inBandBytes.AsSpan(1));

        await _payloadSender.SendPayloadAsync(new OutboundPayloadContext(sessionId.Value, AppId.FileTransferControl, inBandBytes), ct);

        return DomainResult.Success();
    }

    public async ValueTask<DomainResult> PauseTransferAsync(TransferSessionId sessionId, CancellationToken ct = default)
    {
        TransferSession? session = await _sessionRepository.GetSessionByIdAsync(sessionId, ct);
        if (session == null) return DomainResult.Failure(new DomainError("Transfer.NotFound", "Session not found."));

        DomainResult result = session.Pause();
        if (result.IsSuccess) await _sessionRepository.SaveSessionAsync(session, ct);
        return result;
    }

    public async ValueTask<DomainResult> ResumeTransferAsync(TransferSessionId sessionId, CancellationToken ct = default)
    {
        TransferSession? session = await _sessionRepository.GetSessionByIdAsync(sessionId, ct);
        if (session == null) return DomainResult.Failure(new DomainError("Transfer.NotFound", "Session not found."));

        DomainResult result = session.Resume();
        if (result.IsSuccess) await _sessionRepository.SaveSessionAsync(session, ct);
        return result;
    }

    public async ValueTask<DomainResult> CancelTransferAsync(TransferSessionId sessionId, TransferTerminationReason reason, CancellationToken ct = default)
    {
        TransferSession? session = await _sessionRepository.GetSessionByIdAsync(sessionId, ct);
        if (session == null) return DomainResult.Failure(new DomainError("Transfer.NotFound", "Session not found."));

        DomainResult result = session.Cancel(reason, _clock);
        if (result.IsSuccess)
        {
            await _sessionRepository.SaveSessionAsync(session, ct);
            // Proactive tracker unregistration
            SwarmRendezvousId rendezvous = SwarmRendezvousId.Derive(new TransferKey(new byte[32]));
            await _trackerPort.UnregisterAsync(rendezvous, session.InitiatorId, ct);
        }
        return result;
    }
}
```

---

### 5.3 Read Models, Query Service & Stale Offer Reaper

```csharp
namespace Percolator.Apps.FileTransfer.Ports;

public sealed record ManifestFileReadModel(
    FileIndex Index,
    SanitizedRelativePath RelativePath,
    FileSizeBytes FileSize,
    BundleByteOffset StartOffset,
    BundleByteOffset EndOffset,
    FileLastModifiedUtc LastModifiedUtc,
    bool IsSelected,
    bool IsCompleted);

public sealed record TransferSessionReadModel(
    TransferSessionId SessionId,
    RootBundleName RootName,
    PublicIdentityId InitiatorId,
    TransferTopology Topology,
    RootDirectoryPath? LocalRootPath,
    TotalBundleBytes TotalBytes,
    TotalBundleBytes DownloadedBytes,
    TotalBundleBytes UploadedBytes,
    SeedRatio CurrentSeedRatio,
    SeedRatio TargetSeedRatio,
    ChunkCount CompletedChunks,
    ChunkCount TotalChunks,
    TransferBandwidth DownloadSpeed,
    TransferBandwidth UploadSpeed,
    StreamCount ConnectedStreams,
    PeerCount ConnectedPeers,
    TransferSessionState Status,
    TransferRole Role,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? CompletedAtUtc);

public interface IFileTransferQueryService
{
    ValueTask<IReadOnlyList<TransferSessionReadModel>> GetAllTransfersAsync(CancellationToken ct = default);
    ValueTask<TransferSessionReadModel?> GetTransferByIdAsync(TransferSessionId sessionId, CancellationToken ct = default);
    ValueTask<IReadOnlyList<ManifestFileReadModel>> GetManifestFilesAsync(TransferSessionId sessionId, CancellationToken ct = default);
}

public interface ITransferOfferReaperService
{
    ValueTask<int> SweepAndExpireStaleOffersAsync(TimeSpan retentionWindow, CancellationToken ct = default);
}

public sealed class TransferOfferReaperService : ITransferOfferReaperService
{
    private readonly ITransferSessionRepository _sessionRepository;
    private readonly IDateTimeProvider _clock;

    public TransferOfferReaperService(ITransferSessionRepository sessionRepository, IDateTimeProvider clock)
    {
        _sessionRepository = sessionRepository;
        _clock = clock;
    }

    public async ValueTask<int> SweepAndExpireStaleOffersAsync(TimeSpan retentionWindow, CancellationToken ct = default)
    {
        IReadOnlyList<TransferSession> activeSessions = await _sessionRepository.GetActiveSessionsAsync(ct);
        DateTimeOffset now = _clock.GetUtcNow();
        int expiredCount = 0;

        foreach (TransferSession session in activeSessions)
        {
            if (session.State == TransferSessionState.PendingConsent && (now - session.CreatedAtUtc) > retentionWindow)
            {
                session.ExpireOffer(_clock);
                await _sessionRepository.SaveSessionAsync(session, ct);
                expiredCount++;
            }
        }

        return expiredCount;
    }
}
```

---

## 6. Swarm Rendezvous, Fast-Resume & Coordination Algorithms

### 6.1 Rarest-First Chunk Selector Algorithm

```csharp
namespace Percolator.Apps.FileTransfer.Features.SwarmCoordination;

public static class RarestFirstChunkSelector
{
    public static ChunkIndex? SelectNextChunk(
        Bitfield localBitfield,
        Bitfield requiredMask,
        IReadOnlyCollection<SwarmMember> swarmMembers,
        IReadOnlySet<ChunkIndex> inFlightChunks)
    {
        Bitfield missingChunks = requiredMask.Difference(localBitfield);
        if (missingChunks.IsEmpty) return null;

        // Collect unchoked seeders/leechers
        List<SwarmMember> activePeers = [];
        foreach (SwarmMember member in swarmMembers)
        {
            if (member.PeerChokingUs == PeerChokeStatus.Unchoked)
            {
                activePeers.Add(member);
            }
        }

        if (activePeers.Count == 0) return null;

        ChunkIndex? rarestIndex = null;
        int lowestRarity = int.MaxValue;

        for (int i = 0; i < missingChunks.TotalChunks.Count; i++)
        {
            ChunkIndex candidate = ChunkIndex.FromInt(i);
            if (!missingChunks.HasChunk(candidate) || inFlightChunks.Contains(candidate))
                continue;

            int peerCountWithChunk = 0;
            for (int p = 0; p < activePeers.Count; p++)
            {
                if (activePeers[p].Bitfield.HasChunk(candidate))
                    peerCountWithChunk++;
            }

            if (peerCountWithChunk > 0 && peerCountWithChunk < lowestRarity)
            {
                lowestRarity = peerCountWithChunk;
                rarestIndex = candidate;
            }
        }

        return rarestIndex;
    }
}
```

---

### 6.2 `DiskChunkAssembler` Implementation

```csharp
namespace Percolator.Apps.FileTransfer.Features.Storage;

public sealed class DiskChunkAssembler
{
    private readonly IDesktopFileStorage _fileStorage;

    public DiskChunkAssembler(IDesktopFileStorage fileStorage)
    {
        _fileStorage = fileStorage;
    }

    public async ValueTask<DomainResult> AssembleChunkAsync(
        TransferSession session,
        RootDirectoryPath rootPath,
        ChunkIndex chunkIndex,
        PlaintextChunkData plaintextData,
        IDateTimeProvider clock,
        CancellationToken ct = default)
    {
        // Compute file slice boundaries across continuous bundle
        IReadOnlyList<FileChunkSlice> slices = ChunkBoundaryCalculator.CalculateSlices(
            session.Manifest,
            chunkIndex,
            ChunkByteLength.FromBytes(plaintextData.Length));

        // Write each slice to target file on disk
        foreach (FileChunkSlice slice in slices)
        {
            FileEntry entry = session.Manifest.Files[slice.FileIndex.Value];
            ResolvedFilePath targetFile = ResolvedFilePath.Resolve(rootPath, entry.RelativePath);

            ReadOnlyMemory<byte> sliceMemory = plaintextData.Memory.Slice(slice.BufferOffset, slice.SliceLength.Bytes);
            await _fileStorage.WriteChunkAsync(targetFile, slice.OffsetInFile, PlaintextChunkData.FromMemory(sliceMemory), ct);
        }

        // Record chunk verification in domain aggregate (detects file and session completion)
        return session.VerifyAndRecordChunk(chunkIndex, plaintextData, clock);
    }
}
```

---

### 6.3 Swarm Peer Liveness & Heartbeats

```csharp
namespace Percolator.Apps.FileTransfer.Features.SwarmCoordination;

public sealed class SwarmPeerLiveness
{
    public PublicIdentityId PeerId { get; }
    public DateTimeOffset LastSeenUtc { get; private set; }

    public SwarmPeerLiveness(PublicIdentityId peerId, DateTimeOffset lastSeenUtc)
    {
        PeerId = peerId;
        LastSeenUtc = lastSeenUtc;
    }

    public bool IsActive(DateTimeOffset now, TimeSpan timeout) => now - LastSeenUtc <= timeout;
    public void RecordActivity(IDateTimeProvider clock) => LastSeenUtc = clock.GetUtcNow();
}
```

---

## 7. Threat Modeling & Security Posture (Lens 3)

| Threat / Vulnerability | Protocol Defense | Verification Mechanism |
| :--- | :--- | :--- |
| **Recipient Tracking / Probing** | Silent by default: Inbound transfer offers generate zero wire response or ACK unless explicitly accepted. | Unit test asserts zero outbound messages dispatched upon receiving offer. |
| **Parser / Deserialization Exploits** | Edge kill switch (`IAppIngressFilter`): When disabled, raw packets with `AppId = 0x03` are discarded before deserialization. | Unit test asserts parser is uninvoked when plugin is disabled. |
| **Directory Traversal** | Path sanitization rejecting `..`, absolute paths, null bytes, and Windows device names via `SanitizedRelativePath`. | Test suite fuzzes malicious path entries in manifests. |
| **Disk Exhaustion** | Upfront disk space verification (`CheckDiskSpaceAsync` with cancellation) before accepting. | Test asserts rejection if disk quota is exceeded. |
| **IP Address Leakage (Default Relayed)** | Default posture routes WAN transfers strictly via blind relay pipes; direct WAN endpoints (UPnP / Relay STUN) require explicit user opt-in (`WanPolicy == DirectWanPolicy.ExplicitlyAllowed`). | Test asserts WAN IP endpoints are never generated or dispatched unless opted in. |
| **Direct P2P & Swarm IP Exposure** | Intentional protocol design for throughput parity with BitTorrent. Direct socket saturation requires peers to exchange socket candidates, exposing physical IPv4/IPv6 addresses, NAT topologies, and ISP locations to invited peers (`TargetRecipients`). The UI presentation layer explicitly warns users and lists participant identities before opening direct connections. | Unit test asserts WAN IP candidates are never generated or advertised when direct connections are disabled; UI read model exposes participants for privacy prompt. |
| **PEX WAN Policy Bypass / Unauthorized Direct Dialing** | PEX WAN Policy Gate drops all incoming WAN candidates from `PexPeersFrame` if `DirectWanPolicy == DisabledByDefault`, preventing rogue peers from forcing direct internet connections. | Unit test verifies dropping of WAN candidates from PEX frames in relay mode (Lens 3). |
| **Egress WAN IP Leakage during Initiation / Acceptance** | Candidate discovery engine strictly suppresses UPnP/STUN queries and WAN endpoint population unless `wanPolicy == DirectWanPolicy.ExplicitlyAllowed`. | Unit test asserts zero WAN candidates serialized in offer/accept when disabled (Lens 3). |
| **Relay Swarm Tracker Identity Exposure** | Explicit architectural tradeoff for direct contact swarms; bounded by ephemeral 90-second in-memory sliding window, zero disk logging, and proactive unregistration upon session termination. | Integration test verifies in-memory expiration and candidate deletion on transfer complete (Lens 3). |
| **Physical IP-to-Identity Binding / Non-Repudiation** | Raw stream handshakes rely strictly on symmetric HMAC session tokens ($K_{\text{transfer}}$) negotiated inside Double Ratchet. Zero root identity signatures are emitted over TCP, maintaining forward secrecy and cryptographic deniability. | Architecture enforces that private identity keys are never passed to transport socket pools or stream frames (Lens 3). |
| **Static Symmetric Session Key** | Intentional performance design avoiding per-chunk Double Ratchet overhead. AES-256 session key $K_{\text{transfer}}$ is securely negotiated in-band; all bulk streaming chunks share the key. Loss of per-chunk forward secrecy is bounded to that transfer session. | Architecture enforces strict in-band exchange of session key and base nonce; zero ratchet messages used for bulk data. |
| **Piece Possession Leakage** | Intentional BitTorrent swarming requirement; bitfield and `HAVE` broadcasts leak chunk possession to invited peers to enable rarest-first piece scheduling. | Architecture limits piece announcements strictly to authenticated swarm streams. |
| **Chunk Tampering / Substitution** | Dual cryptographic verification: AES-256-GCM authentication tag + SHA-256 leaf hash against Merkle root. | Test asserts corrupted chunk bytes are discarded and requested again. |
| **Poisoned Block / Sybil Torrent Flooding** | Application `TransferSessionCoordinator` maps streams to peer identities; on verification failure, it chokes the peer (`SetWeChokingPeer`), logs an offense, drops the connection, and reschedules the chunk to alternate seeders. | Coordinator unit test verifies that peer streams are disconnected and choked upon receiving corrupt chunks (Lens 3). |
| **Relay Snooping** | E2EE per-chunk encryption ($K_{\text{transfer}}$); relay acts as blind socket proxy without keys. | Test verifies relay cannot decrypt chunks with zero knowledge of session key. |
| **Network Bufferbloat / Choking** | High-precision token bucket bandwidth throttle (`ITransferRateLimiter`). | Unit test asserts throughput throttles accurately to configured caps. |
| **In-Band Ratchet Envelope Overflow** | Strict 48 KB cap (`MaxInlineManifestBytes = 49_152`); manifests exceeding 48 KB must distribute via out-of-band `RemoteBlob`. Preserves headroom in Double Ratchet 64 KB total envelope limit. | Unit test verifies that inline manifests $> 48\text{ KB}$ are rejected by validator and initiation service (Lens 3). |
| **Remote Manifest Blob Exhaustion / Heap Bloat** | Strict 8 MB cap (`MaxRemoteManifestBlobBytes = 8_388_608`) on `RemoteBlob` size; downloader counting stream aborts download if exceeded. | Unit test rejects offer with `PlaintextSizeBytes > 8\text{ MB}` (Lens 3). |
| **Unbounded Manifest Allocation / Deserialization DoS** | Ingress offer bounds (`MaxFilesPerBundle = 50,000`, `MaxChunksPerBundle = 1,000,000`, `MaxBundleSizeBytes = 16 TB`, 48 KB inline limit) reject malformed offers before domain allocation. | Ingress validation unit tests verify rejection of oversized offers (Lens 3). |
| **AES-GCM Nonce Collision / Key-Stream Reuse** | Deterministic nonce derivation $\text{ChunkNonce}(i) = \text{BaseNonce} \oplus \text{BigEndianUInt96}(i)$ guarantees distinct 12-byte nonces for every chunk index $0 \le i < 1,000,000$. | Unit test asserts mathematical uniqueness of nonces across all chunk indices up to bundle maximum (Lens 3). |
| **Chunk Request Queue Flooding (Memory Exhaustion DoS)** | In-flight request queue capped at `MaxPendingRequestsPerPeer = 32`; requests beyond cap are dropped and peer is choked. | Unit test verifies request dropping and choking upon exceeding 32 requests (Lens 3). |
| **PEX Candidate Flooding / Dialing Amplification** | PEX frame rate-limiting (1 frame / 10s per peer), candidate cap (`MaxCandidates = 16`), and Host/Port deduplication protect dialing pools and memory. | Unit test asserts candidate list truncation and deduplication (Lens 3). |
| **Dangling Swarm Tracker Candidates** | Proactive unregistration via `IDistributedTrackerPort.UnregisterAsync` upon cancellation, failure, or completion. | Unit test asserts `UnregisterAsync` invocation when cancelling/completing transfer (Lens 3). |
| **Pre-Consent Offer Hoarding / Storage Bloat** | Background `TransferOfferReaperService` sweeps and expires offers older than 24h via `session.ExpireOffer(clock)`. | Integration test verifies offer reaping after time-advancement (Lens 3). |

---

## 8. Cross-Layer Infrastructure Alignment (Lens 5)

| Application Port / Contract | Infrastructure Adapter / Concrete Artifact | Target Persistence / Transport |
| :--- | :--- | :--- |
| `ITransferStreamPool` | `TcpMultiStreamPool` | 4–8 multiplexed TCP/QUIC sockets (`TCP_NODELAY = true`) |
| `IDistributedTrackerPort` | Distributed Relay Swarm Tracker / PEX Client | Swarm rendezvous on relay or out-of-band PEX |
| `IDesktopFileStorage` | `PortableDesktopFileStore` | Staging with `.percolator-part`, pre-allocation, bitfield scanning, atomic rename |
| `ITransferSessionRepository` | `SqliteTransferSessionRepository` | `ft_transfer_sessions`, `ft_manifest_files`, `ft_swarm_members`, `ft_swarm_endpoints` |
| `IFileTransferFilterPolicyRepository` | `SqliteFilterPolicyRepository` | `ft_filter_policies`, `ft_blocked_peers`, `ft_blocked_relays` |
| `ITransferRateLimiter` | `TokenBucketRateLimiter` | High-precision token bucket bandwidth throttle |
| `IFileTransferQueryService` | `SqlFileTransferQueryService` | Fast-path Dapper/SQL projection for transfer dashboard |
| Ingress Filtering (`IAppIngressFilter`) | `InboundIngressPipeline` Edge Interceptor | Pre-dispatch drop before deserialization or app routing |
| `ITransferOfferReaperService` | `TransferOfferReaperWorker` | Scheduled background worker sweeping expired staged sessions (Lens 3) |
| Retention & Maintenance | `SwarmEndpointRetentionPruner` | Scheduled background worker pruning `ft_swarm_endpoints` older than 7 days |

---

## 9. Phased Application Implementation Milestones

### Milestone 1: Project Alignment, Registration & Ingress Edge Discard
1. Update `Percolator.Apps.FileTransfer.csproj` to reference `Percolator.Apps.FileTransfer.Domain.csproj` and `Percolator.SourceGenerators` analyzer; remove `Percolator.Domain.csproj`.
2. Implement `ServiceCollectionExtensions.cs` registering application dependencies.
3. Implement `FileTransferPlugin` implementing `IAppPlugin` (`AppId.FileTransferControl = 0x03`).
4. Implement `FileTransferIngressFilter` implementing `IAppIngressFilter` (`PluginSdk`) for ingress pre-filtering before deserialization.
5. Implement `FileTransferPayloadHandler` for ingress processing with 1-byte control opcodes (`0x01` Offer, `0x02` Accept, `0x03` Cancel).
6. Implement in-band payloads: `FileTransferOfferPayload`, `FileTransferAcceptPayload`, `FileTransferCancelPayload`.

### Milestone 2: Multi-Stream Engine & Out-of-Band Framing
1. Implement Application Value Objects and Transport Models, strictly verifying **zero implicit or explicit conversion operators** across all types.
2. Define ports: `ITransferStreamPool`, `ITransferStreamPoolFactory`, `ITransferStream`.
3. Implement out-of-band typed frame dispatching via `IStreamFramePayload` and `StreamFrameEnvelope` with 5-byte header (`[1-byte Opcode] [4-byte BigEndian Length]`).
4. Implement `ChunkCryptoService` handling deterministic nonce derivation `ChunkNonce.Derive` and AES-256-GCM chunk authenticated encryption/decryption.
5. Implement `TransferSessionCoordinator` managing active 4–8 streams and bounding in-flight requests to `MaxPendingRequestsPerPeer = 32`.

### Milestone 3: Desktop Disk I/O & Storage Abstraction
1. Define `IDesktopFileStorage` port using `PlaintextChunkData`, `FileLastModifiedUtc`, `RootDirectoryPath`, `ResolvedFilePath`, and `DiskSpacePreflightResult`.
2. Implement `DiskChunkAssembler` handling multi-file boundary spanning, chunk writes, and atomic finalization.
3. Implement selective file downloading chunk range mapping (`SelectedFileIndices`) and selective disk space pre-flight calculation.
4. Implement zero-byte file immediate finalization workflow on transfer start.

### Milestone 4: Swarm Coordination, Distributed Tracker & Seeding Ratios
1. Implement `SwarmCoordinator` managing active `SwarmMember` entities within `TransferSession` and tracking heartbeat/liveness via `SwarmPeerLiveness`.
2. Implement `RarestFirstChunkSelector` and `EndgameCoordinator`.
3. Implement fast-resume endpoint hydration from `ft_swarm_endpoints` and live PEX frame candidate updates.
4. Implement `IDistributedTrackerPort` announce and unregister workflows using `SwarmRendezvousId` and `PublicIdentityId`.
5. Implement seeding ratio tracking ($\text{Uploaded} / \text{Total}$) and automatic disconnect on target ratio.

### Milestone 5: CQRS Transfer Manager Ports & Rate Limiter
1. Implement `IFileTransferActionHandler` in `FileTransferActionHandler` supporting the 5 core lifecycle commands (`InitiateTransferAsync`, `AcceptTransferAsync`, `PauseTransferAsync`, `ResumeTransferAsync`, `CancelTransferAsync`).
2. Implement rate limiter adapter integration with `ITransferRateLimiter` using `BandwidthLimit` and async `ValueTask` caps.
3. Implement `IFileTransferFilterPolicyRepository` port for filter policy persistence taking `ownerId`.
4. Implement `IFileTransferQueryService` providing fast-path dashboard read models.
5. Implement `TransferOfferReaperService` sweeping stale offers older than 24 hours.

### Milestone 6: Infrastructure Adapter Integration & End-to-End Verification
1. Wire `FileTransferPlugin` with `TcpMultiStreamPool` and `PortableDesktopFileStore` from `Percolator.Infrastructure`.
2. Test local LAN P2P multi-stream gigabit transfer.
3. Test opt-in WAN direct connections (simulated UPnP and relay STUN reflection).
4. Test blind relay transfer pipe streaming with simulated latency and packet drop.
5. Test swarm multi-peer piece sharing with 3+ desktop nodes.

---

## 10. High-Value, Low-Brittle Application Unit Testing Suite (Lens 4)

### 10.1 Testing Strategy & Anti-Brittle Verification Invariants
In strict adherence to the Percolator architecture and testing standards, application-layer unit tests focus exclusively on **observable domain outcomes, protocol admission boundaries, state machine transitions, and fault tolerance**.

#### Anti-Brittle Directives:
1. **Zero Mock Invocation Assertions**: Tests never assert internal mock dispatch counts or internal method invocation sequences. Verification is conducted exclusively through observable state transformations, returned `DomainResult` values, event emissions, and outbound port records.
2. **Zero Serialization & String Matching**: Tests never perform fragile string matching against serialized JSON/Protobuf or exception error messages. Tests assert typed domain errors, enum codes, or structured payload properties.
3. **Pure State & Admission Testing**: Subsystems are tested through pure input/output matrices, boundary condition limits, deterministic cryptographic derivation, and fault-isolated batch execution.
4. **Vertical Slice Alignment**: Tests are organized strictly by vertical feature slices (`Features/<FeatureName>/Tests/` and `Ports/Tests/`), matching the production directory topology.
5. **Deterministic Virtualized Clocks**: All temporal evaluations (PEX 10s rate limiting, 30s idle keep-alive, 60s dead peer timeout, 24h offer reaper sweep) must use `IDateTimeProvider` (or .NET's `FakeTimeProvider`) to advance virtual time, explicitly prohibiting real-time wall-clock delays (`Task.Delay`, `Thread.Sleep`).

---

### 10.2 Vertical Slice 1: `Features/IngressControl` Unit Tests

| Test Identifier | Test Scenario | Input / Preconditions | Observable Behavioral Assertion / State Transition | Rationale & Anti-Brittle Justification |
| :--- | :--- | :--- | :--- | :--- |
| **UT-ING-01** | Edge filter silent discard when plugin disabled | `IAppIngressFilter.IsEnabled == false`, inbound packet with `AppId.FileTransferControl` (0x03). | Returns `IngressDecision.DiscardSilently`. Zero parsing, zero allocation, zero downstream dispatch. | Verifies edge kill-switch behavior without inspecting internal filter state. |
| **UT-ING-02** | Ingress peer blocking policy enforcement | Filter policy contains sender `PublicIdentityId` in `BlockedPeers`. | `FileTransferIngressFilter.Evaluate` returns `IngressDecision.DiscardSilently`. | Verifies privacy firewall without emitting error frames on wire. |
| **UT-ING-03** | Ingress relay URI blocking policy enforcement | Inbound offer contains `RelayPipeEndpointUri` present in `BlockedRelays`. | `FileTransferPayloadHandler` drops payload silently; session repository retains zero staged rows. | Verifies unverified relay rejection without wire signaling. |
| **UT-ING-04** | Ingress offer staging for allowed peer | Valid `FileTransferOfferPayload` received from authorized sender not in blocklist. | Staged session is persisted in `ITransferSessionRepository` with `TransferSessionState.PendingConsent`. Exactly zero outbound network packets or wire ACKs emitted. | Proves silent staging requirement (recipient privacy). |
| **UT-ING-05** | Offer rejection on bundle file count overflow | Inbound `FileTransferOfferPayload` specifies `Summary.TotalFiles.Count = 50_001` (> 50,000 cap). | `IngressTransferOfferValidator.ValidateOffer` returns `ValidationResult.Failure`. Session is not staged; repository is uninvoked. | Guards against memory exhaustion DoS before domain object instantiation. |
| **UT-ING-06** | Offer rejection on bundle chunk count overflow | Inbound `FileTransferOfferPayload` specifies `Summary.TotalChunks.Count = 1_000_001` (> 1,000,000 cap). | Returns `ValidationResult.Failure`. Session is not staged. | Enforces symmetric chunk bounds. |
| **UT-ING-07** | Offer rejection on bundle byte size overflow | Inbound `FileTransferOfferPayload` specifies `Summary.TotalSize.Bytes = 16_000_000_000_001L` (> 16 TB cap). | Returns `ValidationResult.Failure`. Session is not staged. | Enforces 16 TB architectural cap. |
| **UT-ING-08** | In-band manifest envelope overflow rejection | Inbound offer with `ManifestDistributionKind.Inline` containing 49,153 bytes (> 48 KB cap). | Returns `ValidationResult.Failure`. Offer is rejected prior to domain allocation. | Preserves 64 KB Double Ratchet headroom. |
| **UT-ING-09** | Remote manifest blob size bounds verification | Inbound offer with `ManifestDistributionKind.RemoteBlob` referencing blob with `PlaintextSizeBytes = 8_388_609L` (> 8 MB cap). | Returns `ValidationResult.Failure`. Offer is rejected prior to fetching blob. | Prevents manifest memory exhaustion. |
| **UT-ING-10** | Inbound cancellation from authorized initiator | Inbound `FileTransferCancelPayload` received matching an active or staged session from `InitiatorId`. | Session transitions to `TransferSessionState.Cancelled` with reason recorded. | Verifies authorized session lifecycle termination. |
| **UT-ING-11** | Inbound cancellation from unauthorized third party | Inbound `FileTransferCancelPayload` received from peer identity not present in `session.Topology`. | Cancellation is discarded silently; session state remains unchanged (`PendingConsent` or `InFlight`). | Prevents unauthorized session disruption. |

---

### 10.3 Vertical Slice 2: `Features/TransferNegotiation` Unit Tests

| Test Identifier | Test Scenario | Input / Preconditions | Observable Behavioral Assertion / State Transition | Rationale & Anti-Brittle Justification |
| :--- | :--- | :--- | :--- | :--- |
| **UT-NEG-01** | Selective file preflight disk space calculation | User accepts transfer selecting files 0 and 2 from 4-file bundle (sizes: 100 MB, 500 MB, 200 MB, 1 GB). | Coordinator queries `IDesktopFileStorage.CheckDiskSpaceAsync` with exactly 300 MB (`RequiredBytes`). If sufficient, transitions session to `InFlight`. | Verifies that unselected files (1.5 GB) are excluded from disk pre-allocation math. |
| **UT-NEG-02** | Insufficient disk space abort | User accepts transfer, but `IDesktopFileStorage.CheckDiskSpaceAsync` returns `DiskSpacePreflightResult.Insufficient`. | `AcceptTransferAsync` returns `DomainResult.Failure`; session state remains `PendingConsent`; no files pre-allocated. | Prevents partial writes when disk space is inadequate. |
| **UT-NEG-03** | Immediate finalization of zero-byte files on start | Transfer bundle contains three files: 0-byte `empty.txt`, 50 MB `data.bin`, 0-byte `readme.txt`. Session transitions to `InFlight`. | Storage port records completion for `empty.txt` and `readme.txt` immediately with zero chunk read/write operations. | Prevents hung transfers on zero-chunk files. |
| **UT-NEG-04** | Egress candidate suppression under default WAN policy | User initiates transfer with `DirectWanPolicy.DisabledByDefault`. | Generated `FileTransferOfferPayload.DirectCandidates` contains zero WAN endpoints (UPnP/STUN); only RFC 1918 LAN candidates present (or empty if LAN disabled). | Verifies Tier 1 default WAN privacy posture. |
| **UT-NEG-05** | Egress candidate gathering under explicit opt-in policy | User initiates transfer with `DirectWanPolicy.ExplicitlyAllowed`. | Generated `FileTransferOfferPayload.DirectCandidates` contains resolved WAN candidates (`DirectCandidateKind.DirectWanUpnp` / `DirectWanStun`). | Verifies Tier 3 opt-in candidate emission. |
| **UT-NEG-06** | Dialing pool fault tolerance on partial batch failure | Coordinator dials 4 endpoint candidates (2 LAN direct, 2 relay pipes). One LAN dial fails with connection timeout; 3 remaining succeed. | Failed connection does not abort transfer; 3 active streams are registered in pool; session state progresses to `InFlight`. | Verifies fault-tolerant batch connection dialing without brittle single-point abort. |
| **UT-NEG-07** | Remote manifest blob deceptive overflow abort | Inbound offer advertises `PlaintextSizeBytes = 500 KB`, but relay HTTP stream emits $> 8\text{ MB}$ of ciphertext. | Counting stream detects byte count exceeding `MaxRemoteManifestBlobBytes` (8 MB), aborts connection immediately, and deletes incomplete payload. | Defends against malicious seeders attempting heap exhaustion via false manifest size claims. |

---

### 10.4 Vertical Slice 3: `Features/MultiStreamEngine` Unit Tests

| Test Identifier | Test Scenario | Input / Preconditions | Observable Behavioral Assertion / State Transition | Rationale & Anti-Brittle Justification |
| :--- | :--- | :--- | :--- | :--- |
| **UT-MSE-01** | Deterministic AES-GCM nonce derivation | Derived `ChunkNonce` calculated for indices $i = 0$, $i = 1$, $i = 999,999$ from fixed `BaseNonce`. | Nonce values match $\text{BaseNonce} \oplus \text{BigEndianUInt96}(i)$; nonces are pairwise distinct across all indices up to maximum. | Verifies mathematical uniqueness avoiding key-stream reuse. |
| **UT-MSE-02** | In-flight request queue bounding per peer | Remote peer sends 33 consecutive `RequestChunkFrame` messages without waiting for responses (`MaxPendingRequestsPerPeer = 32`). | The 33rd request is dropped unfulfilled; coordinator transitions peer flow status to choked (`SetWeChokingPeer`) and pending count remains at 32. | Verifies observable queue bounding and flow control without inspecting private frame emission order. |
| **UT-MSE-03** | Unsolicited chunk data frame drop | Inbound `ChunkDataFrame` arrives for chunk index 42, which was never scheduled or requested by local node. | Chunk payload is discarded immediately; storage port is not invoked; zero disk writes occur. | Protects disk I/O and cache from unrequested data injection. |
| **UT-MSE-04** | Corrupted chunk verification & poisoned peer choking | Inbound `ChunkDataFrame` fails Merkle tree leaf hash or AES-GCM authentication. | Coordinator chokes peer (`SetWeChokingPeer`), increments offense counter, terminates corrupted stream, and reschedules chunk to alternate peer. | Verifies poisoned block containment and swarm self-healing. |
| **UT-MSE-05** | Symmetric stream handshake authentication | Incoming stream connection receives `HandshakeFrame` with valid `StreamAuthToken`, valid `SessionId`, and authorized `SenderPeerId`. | Stream handshake completes successfully; stream is registered in active multi-stream pool. | Verifies symmetric deniable authentication without identity leakage. |
| **UT-MSE-06** | Invalid handshake token socket termination | Incoming stream connection receives `HandshakeFrame` with invalid `StreamAuthToken` or unknown `SessionId`. | Stream socket is terminated immediately; zero payload data is read or processed. | Prevents unauthorized socket hijacking. |
| **UT-MSE-07** | Out-of-order parallel chunk reassembly | Parallel streams deliver chunks out of order (Stream 1 delivers Chunk 4, Stream 2 delivers Chunk 1). | `DiskChunkAssembler` dispatches each chunk to its target file offset independently; `LocalBitfield` marks both chunks; zero chunk stalling occurs. | Validates concurrent non-blocking chunk assembly across parallel streams. |

---

### 10.5 Vertical Slice 4: `Features/SwarmCoordination` Unit Tests

| Test Identifier | Test Scenario | Input / Preconditions | Observable Behavioral Assertion / State Transition | Rationale & Anti-Brittle Justification |
| :--- | :--- | :--- | :--- | :--- |
| **UT-SWM-01** | Rarest-first chunk scheduling across swarm | 3 peers connected. Chunk 0 is available on 3 peers; Chunk 1 is available on 1 peer; Chunk 2 is available on 2 peers. | `RarestFirstChunkSelector` schedules Chunk 1 first, maximizing swarm piece availability. | Validates piece dissemination optimization. |
| **UT-SWM-02** | PEX frame ingestion rate limiting via virtual clock | Remote peer sends two `PexPeersFrame` messages separated by 4 seconds on `IDateTimeProvider` virtual clock (limit is 1 frame / 10s). | Second PEX frame is dropped without processing candidates or querying storage. | Prevents PEX dialing amplification attacks with zero wall-clock wait. |
| **UT-SWM-03** | PEX WAN policy gate enforcement | Inbound `PexPeersFrame` contains 2 LAN candidates and 2 public WAN candidates during session with `DirectWanPolicy.DisabledByDefault`. | LAN candidates are accepted; WAN candidates are discarded immediately and not persisted to `ft_swarm_endpoints`. | Enforces strict relay privacy firewall against remote candidate injection. |
| **UT-SWM-04** | Swarm peer heartbeat & idle keep-alive via virtual clock | Connected stream has zero chunk traffic while virtual clock is advanced by 30 seconds. | `SwarmCoordinator` transmits 0-byte `KeepAliveFrame` over the idle stream. | Maintains NAT bindings without payload overhead. |
| **UT-SWM-05** | Dead peer detection and endpoint archival via virtual clock | Virtual clock is advanced by 60 seconds with zero frames received from connected peer. | Peer connection is marked dead, sockets are closed, and peer's `last_seen_utc` is archived in SQLite `ft_swarm_endpoints`. | Cleans up dead connections and prepares fast-resume cache. |
| **UT-SWM-06** | Target seed ratio auto-completion | Transfer session configured with `TargetSeedRatio = 1.5`. Cumulative uploaded bytes reach $1.5 \times \text{TotalBytes}$. | Session transitions to `TransferSessionState.Completed`; seeding streams disconnect cleanly; tracker is unregistered. | Enforces bandwidth quota policy without manual user intervention. |
| **UT-SWM-07** | Endgame mode chunk broadcast & cancellation | Only 2 chunks remain uncompleted in transfer; active request to Peer A is slow. | Coordinator broadcasts `RequestChunkFrame` for remaining chunks to all unchoked seeders (Peers A and B). Upon receiving chunk from Peer B, coordinator immediately broadcasts `CancelChunkFrame` to Peer A. | Prevents tail latency in multi-peer swarm transfers. |
| **UT-SWM-08** | PEX candidate deduplication & self-endpoint filtering | Inbound `PexPeersFrame` contains duplicate host+port entries and local node's own listening address. | Coordinator deduplicates candidates and strips local node endpoint before persisting to `ft_swarm_endpoints`. | Prevents self-dialing loops and redundant connection attempts. |

---

### 10.6 Vertical Slices 5 & 6: Application Ports & CQRS Unit Tests

| Test Identifier | Test Scenario | Input / Preconditions | Observable Behavioral Assertion / State Transition | Rationale & Anti-Brittle Justification |
| :--- | :--- | :--- | :--- | :--- |
| **UT-CMD-01** | Initiate transfer bounds enforcement | User initiates transfer from folder containing 50,001 files. | `IFileTransferActionHandler.InitiateTransferAsync` returns `DomainResult.Failure`; zero database records created. | Validates command boundary enforcement. |
| **UT-CMD-02** | Proactive tracker endpoint unregistration on cancellation | Active transfer session is cancelled via `CancelTransferAsync`. | Coordinator dispatches `IDistributedTrackerPort.UnregisterAsync(RendezvousId, LocalPeerId)`; session state is `Cancelled`. | Prevents dangling stale IP endpoints on relay swarm tracker. |
| **UT-REP-01** | Stale offer reaping service sweep via virtual clock | `ITransferSessionRepository` contains two offers in `PendingConsent`: one created 25 hours ago, one created 2 hours ago. `IDateTimeProvider` clock matches current time. | `TransferOfferReaperService.SweepAndExpireStaleOffersAsync` expires the 25-hour offer (transitions to `Cancelled`), leaving the 2-hour offer intact. | Prevents staged offer hoarding and database accumulation with zero wall-clock delay. |
| **UT-QRY-01** | Transfer session query projection accuracy | Query service retrieves `TransferSessionReadModel` for multi-file transfer in progress. | Read model accurately projects `TotalBytes`, `DownloadedBytes`, `UploadedBytes`, `CurrentSeedRatio`, `ConnectedStreams`, and `Status` without exposing secrets. | Ensures UI presentation layer receives strictly sanitized, typed progress data. |

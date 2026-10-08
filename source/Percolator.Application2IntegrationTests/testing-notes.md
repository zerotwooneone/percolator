# Integration Testing Notes: Lessons Learned, Anti-Patterns & Reusable Patterns

> **Notice**: This document preserves architectural knowledge, testing techniques, and reusable test code captured from `Percolator.ApplicationIntegrationTests` (v1) prior to its deprecation and deletion, and defines the testing architecture and builder patterns for `Percolator.Application2IntegrationTests`.

---

## 1. Executive Summary & Context

`Percolator.ApplicationIntegrationTests` attempted to perform end-to-end multi-node integration testing against Percolator's legacy v1 application layer. 

While the test suite ultimately suffered from coupling and flakiness due to v1's monolithic host design, it pioneered several valuable testing utilities (ephemeral in-memory certificates, dynamic loopback port assignment, structured log capturing, and gRPC call context test doubles).

This document serves three purposes:
1. **Document the anti-patterns and failure modes** of v1 integration testing so we avoid repeating them in `Percolator.Application2IntegrationTests`.
2. **Preserve exact, battle-tested source code snippets** of the testing utilities so they can be adapted directly into the cross-platform v2 test suite.
3. **Formalize the Fluent Builder Pattern** to dramatically reduce test setup boilerplate and eliminate duplication across multi-node test scenarios.

---

## 2. Lessons Learned & Architectural Anti-Patterns

### 2.1 The "Mocking Mountain" of Monolithic Hosts
In v1, `Percolator.Application` was a monolithic dependency magnet (`DeliverOpaqueMessageHandler` had direct dependencies on Chat, Prekey, DHT, MessageQueue, and Network).
- **The Symptom**: In `DhtIntegrationTests.cs`, testing a single DHT `PingRequest` required mocking **11 separate repositories and services** (`IDhtNodeRepository`, `IDirectSessionRepository`, `ISecureMessagingService`, `IMessageQueueService`, `IRatchetKeyIndex`, `IDirectConversationRepository`, `IPreKeyBundleRepository`, `ISigningService`, `IPeerTrustManager`, `IPeerRoutingProfileRepository`, etc.).
- **The Result**: The test was so brittle that it was permanently annotated with `[Ignore("implementation tests not working yet")]`. Furthermore, 4 out of 12 test files were left as empty 0-byte files (`DhtEndToEndTests.cs`, `DhtProbeLoopbackTests.cs`, `Phase17CommandsOnlyTests.cs`).
- **Lesson for v2**: In the new onion microkernel architecture, `Percolator.Application2` treats application payloads as opaque byte buffers tagged with a 1-byte `AppId`. To test an application plugin (like `Apps.Chat` or `Apps.Discovery`), you only test that specific plugin against `PluginSdk` and its dedicated domain ports—never by bootstrapping an entangled monolith.

### 2.2 Setup Duplication & Missing Builder Abstractions
In v1, every single test method had to write 30–50 lines of imperative ceremony just to construct a test environment:
- Manually instantiating cryptographic keys, device IDs, ports, and identities.
- Manually resolving database contexts, calling `EnsureDeletedAsync()` and `EnsureCreatedAsync()`.
- Copying and pasting the same identity-registration and session-registration blocks across tests.
- **The Result**: High friction to write new tests, copy-paste errors, and tests that obscured the actual behavior being asserted under a mountain of arrangement code.
- **Lesson for v2**: Use the **Builder Pattern** for all test entities (nodes, clusters, pre-key bundles, envelopes, and outbound contexts). Tests should specify only the invariants relevant to their test case, with sensible defaults for everything else.

### 2.3 Hidden Windows-Specific Dependencies Break Cross-Platform CI
The v1 test suite and its underlying dependencies (`Percolator.Identity`, `Percolator.Application`) were tightly bound to Windows APIs:
- `DataProtectionScope.CurrentUser` (DPAPI)
- `FileSystemAclExtensions.GetAccessControl` / `SetAccessControl` (NTFS ACLs)
- `FileSystemAccessRule` / `FileSystemRights.FullControl`
- `WindowsIdentity.User`
- Target framework: `net10.0-windows`
- **Lesson for v2**: `Application2` and `Domain` must remain strictly cross-platform (`net10.0`). Integration tests must run seamlessly across Windows, Linux, and macOS. Cryptographic secrets must be managed using cross-platform software primitives (`ICryptoEngine`, AES-256-GCM, libsodium/NSec), never OS-specific DPAPI or NTFS permissions.

### 2.4 Socket Leaks, Port Collisions & Hanging Test Runners
Running full ASP.NET Core / Kestrel gRPC web hosts in test runners often causes hanging processes:
- Tests in v1 required defensive timeouts: `[Test, CancelAfter(30000)]`.
- Hosts held onto socket bindings and database locks, requiring explicit calls to `GC.Collect()` and `GC.WaitForPendingFinalizers()` in teardowns.
- **Lesson for v2**: 
  1. Reserve real TCP/HTTP/2 Kestrel socket listeners only for a small set of transport smoke tests in `Percolator.Infrastructure2`.
  2. For multi-node application-layer integration tests (`Application2IntegrationTests`), interconnect nodes using an in-memory loopback transport adapter (`IStreamRegistry` and in-memory message channels). This provides 100x faster execution and zero socket port exhaustion.

### 2.5 Moq / Dynamic Proxy Incompatibility with `ReadOnlySpan<byte>`
Documented in v1 `GroupMessageCryptographyServiceTests`:
```csharp
// Manual test double since Moq cannot mock methods with ReadOnlySpan<byte> parameters
```
- **The Problem**: Dynamic proxy mocking libraries (Moq, NSubstitute, FakeItEasy) rely on runtime reflection and IL generation. They cannot mock methods that accept `Span<T>` or `ReadOnlySpan<T>` because `ref struct` types cannot be boxed or placed on generic type parameters.
- **Lesson for v2**: Never attempt to use Moq for crypto engines, wire packers, or byte buffers. Always write strongly-typed hand-rolled test doubles (e.g., `ApplicationTestCryptoEngine`, `FakeSessionWirePacker`, `InMemoryRatchetSessionRepository`).

---

## 3. Reusable Test Utilities (Preserved from v1)

The following utility implementations were captured from `Percolator.ApplicationIntegrationTests` and can be ported directly to `Application2IntegrationTests`.

### 3.1 Ephemeral In-Memory Client Certificate Generator
Generates self-signed ECDSA NIST P-256 x509 certificates purely in memory for testing TLS/mTLS client authentication, eliminating the need to maintain committed `.pfx` files:

```csharp
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

public static class TestCertificateGenerator
{
    public static X509Certificate2 CreateClientCertificate(string commonName = "CN=Percolator.TestClient")
    {
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var req = new CertificateRequest(commonName, ecdsa, HashAlgorithmName.SHA256);
        req.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, false));
        return req.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1), 
            DateTimeOffset.UtcNow.AddYears(1));
    }
}
```

### 3.2 Dynamic OS-Assigned Loopback Port Allocation
Safely retrieves an available TCP port from the OS kernel to eliminate port conflicts:

```csharp
using System.Net;
using System.Net.Sockets;

public static class NetworkTestUtilities
{
    public static int GetAvailablePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
```

### 3.3 Structured In-Memory Log Capturing (`TestLoggerProvider`)
Captures application log entries in a thread-safe in-memory queue to assert security violations, diagnostic traces, and audit logs:

```csharp
using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

public sealed class TestLoggerProvider : ILoggerProvider
{
    private readonly ConcurrentDictionary<string, TestLogger> _loggers = new();
    private readonly ConcurrentQueue<LogEntry> _logEntries = new();
    private readonly ConcurrentBag<string> _logMessages = new();
    private bool _disposed;

    public IReadOnlyList<LogEntry> LogEntries => _logEntries.ToArray();
    public IEnumerable<string> GetAllLogMessages() => _logMessages.ToList();

    public ILogger CreateLogger(string categoryName)
    {
        return _loggers.GetOrAdd(categoryName, name => new TestLogger(name, this, _logMessages));
    }

    internal void AddLogEntry(LogEntry logEntry)
    {
        _logEntries.Enqueue(logEntry);
    }

    public bool ContainsLog(string partialMessage, LogLevel? level = null)
    {
        return _logEntries.Any(entry =>
            entry.Message.Contains(partialMessage, StringComparison.OrdinalIgnoreCase) &&
            (!level.HasValue || entry.LogLevel == level.Value));
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _loggers.Clear();
            _disposed = true;
        }
    }

    private sealed class TestLogger : ILogger
    {
        private readonly string _name;
        private readonly TestLoggerProvider _provider;
        private readonly ConcurrentBag<string> _logMessages;

        public TestLogger(string name, TestLoggerProvider provider, ConcurrentBag<string> logMessages)
        {
            _name = name;
            _provider = provider;
            _logMessages = logMessages;
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var message = formatter(state, exception);
            _provider.AddLogEntry(new LogEntry
            {
                LogLevel = logLevel,
                EventId = eventId,
                Message = message,
                Exception = exception,
                CategoryName = _name,
                Timestamp = DateTimeOffset.UtcNow
            });

            _logMessages.Add(message);
        }
    }

    public sealed class LogEntry
    {
        public LogLevel LogLevel { get; init; }
        public EventId EventId { get; init; }
        public string Message { get; init; } = string.Empty;
        public Exception? Exception { get; init; }
        public string CategoryName { get; init; } = string.Empty;
        public DateTimeOffset Timestamp { get; init; }
    }
}
```

### 3.4 Direct gRPC Context Double (`TestServerCallContext`)
Enables direct invocation and unit/integration testing of gRPC service handlers without spinning up a full HTTP/2 listener:

```csharp
using Grpc.Core;

public sealed class TestServerCallContext : ServerCallContext
{
    private readonly CancellationToken _cancellationToken;
    private readonly string _peer;

    public TestServerCallContext(CancellationToken cancellationToken = default)
        : this("ipv4:127.0.0.1:5000", cancellationToken)
    {
    }

    public TestServerCallContext(string peer, CancellationToken cancellationToken = default)
    {
        _peer = peer;
        _cancellationToken = cancellationToken;
    }

    protected override string MethodCore => "TestMethod";
    protected override string HostCore => "localhost";
    protected override string PeerCore => _peer;
    protected override DateTime DeadlineCore => DateTime.UtcNow.AddMinutes(5);
    protected override Metadata RequestHeadersCore => new Metadata();
    protected override CancellationToken CancellationTokenCore => _cancellationToken;
    protected override Metadata ResponseTrailersCore => new Metadata();
    protected override Status StatusCore { get; set; } = Status.DefaultSuccess;
    protected override WriteOptions? WriteOptionsCore { get; set; }
    protected override AuthContext AuthContextCore => new AuthContext("peer", new Dictionary<string, List<AuthProperty>>());

    protected override ContextPropagationToken CreatePropagationTokenCore(ContextPropagationOptions? options)
    {
        throw new NotSupportedException();
    }

    protected override Task WriteResponseHeadersAsyncCore(Metadata responseHeaders)
    {
        return Task.CompletedTask;
    }
}
```

---

## 4. Fluent Builder Architecture (Eliminating Test Duplication)

To prevent the boilerplate explosion of v1, `Percolator.Application2IntegrationTests` standardizes on **Fluent Builders** for constructing nodes, clusters, cryptographic bundles, and payload contexts.

### 4.1 Node & Cluster Builder (`TestClusterBuilder`)
Provides declarative, one-line setup for multi-node integration test fixtures:

```csharp
// Example Usage in a Test:
[Test]
public async Task DirectRatchetChat_BetweenAliceAndBob_Succeeds()
{
    // Arrange: Create a connected 2-node cluster with a single fluent call
    await using var cluster = await new TestClusterBuilder()
        .AddNode("Alice", node => node.WithAutoApprovedContact("Bob"))
        .AddNode("Bob",   node => node.WithAutoApprovedContact("Alice"))
        .WithInMemoryNetwork()
        .BuildAsync();

    var alice = cluster.GetNode("Alice");
    var bob = cluster.GetNode("Bob");

    // Act
    var sendResult = await alice.SendChatAsync("Bob", "Hello Bob!");

    // Assert
    sendResult.IsSuccess.Should().BeTrue();
    bob.ReceivedChatMessages.Should().ContainSingle(m => m.Content == "Hello Bob!");
}
```

#### Builder Implementation Pattern:
```csharp
public sealed class TestClusterBuilder
{
    private readonly Dictionary<string, Action<TestNodeBuilder>> _nodeConfigs = new();
    private bool _useInMemoryNetwork = true;

    public TestClusterBuilder AddNode(string name, Action<TestNodeBuilder>? configure = null)
    {
        _nodeConfigs[name] = configure ?? (_ => {});
        return this;
    }

    public TestClusterBuilder WithInMemoryNetwork()
    {
        _useInMemoryNetwork = true;
        return this;
    }

    public async Task<TestCluster> BuildAsync()
    {
        var networkSwitch = new InMemoryNetworkSwitch();
        var nodes = new Dictionary<string, ApplicationTestNode>();

        foreach (var (name, configure) in _nodeConfigs)
        {
            var nodeBuilder = new TestNodeBuilder(name, networkSwitch);
            configure(nodeBuilder);
            var node = await nodeBuilder.BuildAsync();
            nodes[name] = node;
        }

        return new TestCluster(nodes, networkSwitch);
    }
}

public sealed class TestNodeBuilder
{
    private readonly string _name;
    private readonly InMemoryNetworkSwitch _network;
    private readonly List<string> _contactsToApprove = new();
    private ICryptoEngine _crypto = new ApplicationTestCryptoEngine();
    private IDateTimeProvider _time = new TestDateTimeProvider();

    public TestNodeBuilder(string name, InMemoryNetworkSwitch network)
    {
        _name = name;
        _network = network;
    }

    public TestNodeBuilder WithCrypto(ICryptoEngine crypto)
    {
        _crypto = crypto;
        return this;
    }

    public TestNodeBuilder WithAutoApprovedContact(string contactName)
    {
        _contactsToApprove.Add(contactName);
        return this;
    }

    public async Task<ApplicationTestNode> BuildAsync()
    {
        var node = new ApplicationTestNode(_name, _network, _crypto, _time);
        await node.InitializeAsync();
        return node;
    }
}
```

### 4.2 Cryptographic Data Builders (`PreKeyBundleBuilder`)
Eliminates repetitive cryptographic key generation and serialization across tests:

```csharp
public sealed class PreKeyBundleBuilder
{
    private PublicIdentityId _identityId = PublicIdentityId.New();
    private DeviceId _deviceId = DeviceId.Primary;
    private uint _signedPreKeyId = 1;
    private uint? _oneTimePreKeyId = 100;
    private DateTimeOffset _expiresUtc = DateTimeOffset.UtcNow.AddMonths(1);

    public PreKeyBundleBuilder ForIdentity(PublicIdentityId id) { _identityId = id; return this; }
    public PreKeyBundleBuilder ForDevice(DeviceId deviceId) { _deviceId = deviceId; return this; }
    public PreKeyBundleBuilder WithoutOneTimePreKey() { _oneTimePreKeyId = null; return this; }
    public PreKeyBundleBuilder Expired() { _expiresUtc = DateTimeOffset.UtcNow.AddDays(-1); return this; }

    public (PreKeyBundle Bundle, EphemeralPrivateKey SignedPreKeyPrivate, EphemeralPrivateKey? OneTimeKeyPrivate) Build(ICryptoEngine crypto)
    {
        var (idPriv, idPub) = crypto.GenerateEphemeralKeyPair();
        var (spkPriv, spkPub) = crypto.GenerateEphemeralKeyPair();
        var signature = crypto.SignEd25519(idPriv.Span, spkPub.Span);

        DhPublicKey? opkPub = null;
        EphemeralPrivateKey? opkPriv = null;
        if (_oneTimePreKeyId.HasValue)
        {
            (opkPriv, opkPub) = crypto.GenerateEphemeralKeyPair();
        }

        var bundle = new PreKeyBundle(
            _identityId,
            _deviceId,
            IdentityKey.FromSpan(idPub.Span),
            _signedPreKeyId,
            spkPub,
            signature,
            _expiresUtc,
            _oneTimePreKeyId,
            opkPub);

        return (bundle, spkPriv, opkPriv);
    }
}
```

### 4.3 Envelope & Context Builders (`InboundEnvelopeBuilder`, `OutboundContextBuilder`)
Allows tests to construct complex envelopes with sensible defaults and override only what is under test:

```csharp
public sealed class InboundDirectEnvelopeBuilder
{
    private ChannelId _channelId = ChannelId.New();
    private PublicIdentityId _recipientId = PublicIdentityId.New();
    private PublicIdentityId _senderId = PublicIdentityId.New();
    private DeviceId _senderDeviceId = DeviceId.Primary;
    private uint _counter = 0;
    private byte[] _ciphertext = new byte[] { 1, 2, 3, 4 };
    private byte[] _nonce = new byte[12];
    private DateTimeOffset _receivedAt = DateTimeOffset.UtcNow;

    public InboundDirectEnvelopeBuilder WithChannel(ChannelId id) { _channelId = id; return this; }
    public InboundDirectEnvelopeBuilder WithSender(PublicIdentityId id) { _senderId = id; return this; }
    public InboundDirectEnvelopeBuilder WithCounter(uint counter) { _counter = counter; return this; }
    public InboundDirectEnvelopeBuilder WithCiphertext(byte[] ciphertext) { _ciphertext = ciphertext; return this; }
    public InboundDirectEnvelopeBuilder WithOversizedPayload(int sizeBytes = 70 * 1024) 
    { 
        _ciphertext = new byte[sizeBytes]; 
        return this; 
    }

    public InboundDirectEnvelope Build(DhPublicKey ephemeralKey)
    {
        var header = new RatchetHeader(ephemeralKey, _counter, 0);
        return new InboundDirectEnvelope(
            _channelId,
            _recipientId,
            _senderId,
            _senderDeviceId,
            header,
            _nonce,
            _ciphertext,
            _receivedAt);
    }
}
```

---

## 5. Blueprint for `Percolator.Application2IntegrationTests`

With the clean microkernel architecture in `Percolator.Application2` and the fluent builders described above, integration tests can run entirely in memory with sub-second execution speeds.

### Recommended Multi-Node End-to-End Scenarios

1. **Scenario 1: Non-Interactive X3DH Handshake & Greeting Preservation**
   - Alice retrieves Bob's `PreKeyBundle` using `PreKeyBundleBuilder`.
   - Alice executes `HandshakeService.InitiateHandshakeAsync` with initial greeting `"Hello Bob"`.
   - Bob receives `InboundHandshakeEnvelope`, validates against `IHandshakeReplayFilter`.
   - Because Alice is not yet an approved contact, Bob's `IPendingHandshakeRepository` saves the envelope.
   - Bob calls `ContactRequestCoordinator.ApproveRequestAsync`.
   - Bob calls `HandshakeService.CompletePendingHandshakeAsync`: verifies ratchet derivation, decrypts greeting, and dispatches to `IAppRouter`.

2. **Scenario 2: Double Ratchet Ingress/Egress Ping-Pong (`AppId.Chat`)**
   - Cluster created via `TestClusterBuilder`.
   - Alice sends a message via `IPayloadSender.SendPayloadAsync(context)`.
   - Assert "Always Outbox First" transactional write in Alice's `IOutboxRepository`.
   - Fast-path push transmits message to Bob via `IStreamRegistry`.
   - Bob's `IInboundIngressPipeline` processes envelope:
     - Validates size $\le 64\text{ KB}$.
     - Checks `IIngressFilterService`.
     - Acquires `IChannelLockService`.
     - Advances receiving DH ratchet.
     - Authenticates AES-GCM Associated Data.
     - Routes to `ChatPayloadHandler`.
     - Deterministically zeroes plaintext in `finally` block.
   - Bob sends reply back to Alice; asserts DH ratchet turnaround.

3. **Scenario 3: Signal Group Sender Key Distribution & Broadcast**
   - Cluster created with 3 nodes: Alice, Bob, Charlie.
   - Alice creates a `GroupChannel`.
   - Alice invokes `GroupKeyDistributionService.DistributeSenderKeyAsync` targeting Bob and Charlie.
   - System distributes keys pairwise via 1:1 channels (`AppId.SystemControl = 0x00`).
   - Bob and Charlie process inbound system messages and import sender keys into `IGroupReceiverSessionRepository`.
   - Alice encrypts group message using `GroupSenderKeyRatchet`, signs with Ed25519, and broadcasts.
   - Bob and Charlie decrypt and verify Alice's author signature.

4. **Scenario 4: Bulk File Transfer Coordination (`AppId.FileTransferControl`)**
   - Alice generates a `TransferManifest` (1 MB Merkle tree chunks) and uploads to `IBlobStorageService`.
   - Alice sends `FileTransferOfferDto` in-band to Bob.
   - Bob downloads the manifest blob and selects files for download.
   - Nodes negotiate out-of-band streaming channels without bloating the Double Ratchet state.

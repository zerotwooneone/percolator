# TLS 2.0 Implementation Plan

## Part 1: Goal and Technical Details

**Goal:**
Replace the insecure shared development certificate with a cryptographically sound TLS implementation using ephemeral certificates. The core technical mechanism relies on generating an ephemeral, disposable TLS certificate mapped 1:1 to the durable SelfIdentity. Because trust is established entirely in the inner X3DH payload, the outer TLS certificate is strictly for transport obfuscation and requires no mathematical link to the X3DH Identity Key.

**Technical Details:**
1.  **Certificate Generation (Disk-Based on Windows):** The existing `CertificateGenerator.CreateTlsCertificate` generates an ephemeral TLS certificate mapped 1:1 to the SelfIdentity. Because trust is established entirely in the inner X3DH payload, the TLS certificate itself is completely disposable. **On Windows**, we must write the certificate to disk (PFX file) because Windows SChannel (which runs in the LSASS process) cannot access in-memory ephemeral keys from the .NET process. 
2.  **Privacy via TLS 1.3:** We enforce `SslProtocols.Tls13` exclusively on both client and server. TLS 1.3 encrypts the certificate payload during the handshake, protecting the Identity Key from passive eavesdroppers (ISPs, packet sniffers). Active scanners can still initiate a handshake to discover the Identity Key, but this is acceptable given that UDP discovery already broadcasts identity information.
3.  **Server Hosting (Kestrel):** At startup, each Node uses its single active `SelfIdentity` context to retrieve or generate an ECDSA (P-256) TLS certificate for its dedicated `MessageListenerService` gRPC listener. The certificate is persisted to disk and loaded by Kestrel.
4.  **Client Transport (gRPC):** Outbound connections via `GrpcMessageTransportService` enforce TLS 1.3 and use a "blind trust" `RemoteCertificateValidationCallback` that accepts any certificate. The actual security is provided by the X3DH/Signal protocol layer, not TLS certificate validation.
5.  **Security Model:** We rely entirely on X3DH (Double Ratchet) for end-to-end encryption and peer authentication. TLS provides transport-layer encryption and privacy (via TLS 1.3 certificate payload encryption), but we do not perform TOFU at the TLS layer. A MitM attacker could intercept the TLS connection but cannot read or forge messages due to X3DH.
6.  **Certificate Lifecycle:** Each self identity has exactly one TLS certificate. Certificates are automatically rotated when they exceed a configurable age threshold (default 90 days).

---

## Part 2: DDD / Clean Architecture Design

The ideal design treats `Percolator.Identity` as the sole authority on trust. It ensures that network discovery layers do not hold duplicate trust stores, and transport layers dynamically resolve trust through the identity aggregates.

### A. Delete Obsolete Code & Database Tables
*   **Duplicate Trust Stores:** `Percolator.Network.ITrustedPeerStore`, `Percolator.Infrastructure.Network.FileBasedTrustedPeerStore`, and `Percolator.Infrastructure.Network.Trust.InMemoryPeerTrustStore`. 
*   **TLS Certificate Persistence:** `Percolator.Infrastructure.Network.Tls.SharedCertificateManager` and `Percolator.Infrastructure.Cryptography.FileBasedCertificateFactory`.
*   **Handshake Services:** `Percolator.Infrastructure.Network.Tls.ITlsHandshakeService` and `Percolator.Infrastructure.Network.Tls.TlsHandshakeService`.
*   **Configuration:** Remove the generic `"percolator-grpc"` HttpClient registration in `ServiceCollectionExtensions.cs`.
*   **Storage Models (Database Migration Required):** Remove the `TlsCertificate` storage property from `PeerRoutingProfile`. This requires removing `PeerRoutingTlsCertificates` from `PercolatorDbContext` and `SqlitePeerRoutingProfileRepository`, and generating an EF Core migration (`dotnet ef migrations add RemovePeerTlsCertificates`) to drop the table.

### B. Transport Certificate Provider (`Percolator.Infrastructure`)
The certificate provider handles generation, disk loading, and rotation of transport certificates. This is purely an Infrastructure concern.

```csharp
public interface ITransportCertificateProvider
{
    // Encapsulates generation, disk loading, and the 90-day rotation completely
    Task<X509Certificate2> GetValidCertificateAsync(SelfIdentity identity, CancellationToken ct);
}
```

*Implementation Details (`Percolator.Infrastructure.Network`):*
1. Generate a completely independent, random ECDSA (P-256) key pair for the TLS certificate to force Windows to use modern CNG KSP, avoiding legacy CSP issues with TLS 1.3
2. Generate certificate with minimal TLS 1.3 extensions:
   - BasicConstraints (CA=false, critical)
   - KeyUsage (DigitalSignature only, critical)
   - EKU (ServerAuthentication, non-critical)
   - SAN (localhost and loopback IPs, non-critical)
3. Export to PFX with static internal password (e.g., "percolator-node-transport")
4. Write to Percolator directory (same location as percolator.db for SQLite)
5. Load with `X509CertificateLoader.LoadPkcs12FromFile(path, password, X509KeyStorageFlags.Exportable | X509KeyStorageFlags.PersistKeySet)`
6. Certificate file path is deterministic: `Percolator/tls_cert_{identityId}.pfx`
7. Check `File.Exists(path)` before attempting to read metadata
8. If file missing, immediately enter generation flow
9. If file exists, load the certificate and check `cert.NotBefore` (to calculate age) or `cert.NotAfter` (to check actual expiration) - do not rely on file system metadata
10. Implement age-based rotation (default 90 days, configurable)
11. No database writes for certificate lifecycle - purely file-based

### C. Network Environment (`Percolator.Application`)
Provides network-related utilities for the Application layer.

```csharp
public interface INetworkEnvironment
{
    // Gets an available port in the specified range
    Task<int> GetAvailablePortAsync(int startRange, int endRange, CancellationToken ct);
}
```

*Implementation Details (`Percolator.Infrastructure.Network`):*
1. Configurable port range (default: 50000-50100)
2. Scan the range for available ports:
   - Check TCP port availability via `System.Net.NetworkInformation.IPGlobalProperties.GetActiveTcpListeners()`
   - Check against existing identity port assignments in the database
3. Return the first available port

**Note:** Interface is defined in `Percolator.Application.Network` to avoid layering violations, implemented in `Percolator.Infrastructure.Network`.

*Application Layer (`Percolator.Application`):*
In the `CreateSelfIdentityCommandHandler`:
1. Call `INetworkEnvironment.GetAvailablePortAsync()`
2. Pass the port into the `SelfIdentity` constructor
3. Save the valid aggregate to the repository

*Domain Model:* Add `ListeningPort` as a mutable property on `SelfIdentity`. TCP ports are volatile OS resources that can become unavailable after reboot, so the port must be updatable during bootstrap.

*Bootstrap Verification:* During application bootstrap, verify the saved port is still available. If not, request a new port from `INetworkEnvironment` and update the aggregate.

### D. Certificate Password
Certificate PFX files are protected with a static internal constant password.

*Implementation Details (`Percolator.Infrastructure.Network`):*
1. Use a static constant password (e.g., "percolator-node-transport")
2. The certificate is self-signed, never transmitted over the wire, and only read by the same machine that created it
3. No database storage required - this is purely an OS-level workaround for Windows SChannel
4. If Data-at-Rest encryption is required, use Windows DPAPI to encrypt the PFX on disk instead of storing the password in the database

### E. Infrastructure Channel Factory (`Percolator.Infrastructure`)
```csharp
public interface IPeerGrpcChannelFactory
{
    GrpcChannel CreateChannel(GrpcEndPoint endpoint);
}
```
*Note:* Local peer identity is not required because client connections use blind trust and do not require mTLS or client certificates.
*Implementation Details:*
Maintains a `ConcurrentDictionary<string, GrpcChannel>` keyed by the physical URI string (e.g., "https://192.168.1.50:50001"). This prevents the "Stale Channel" bug where a peer's IP changes but the factory continues using the cached channel to the old address. The Infrastructure layer operates as a "dumb pipe router" - it only knows about physical addresses, not logical identities. The Application layer is responsible for translating "Who" (PeerId) to "Where" (GrpcEndPoint). Instantiates a `SocketsHttpHandler` with:
*   `SslOptions.EnabledSslProtocols = SslProtocols.Tls13`
*   `SslOptions.RemoteCertificateValidationCallback = (sender, cert, chain, errors) => true` (Blind Trust)
*   No client certificate required (server-side validation only)

*Application Layer Usage:*
```csharp
var targetPeer = await _peerRepository.GetByIdAsync(request.PeerId);
var endpoint = targetPeer.CurrentNetworkEndpoint; // Gets the latest known IP/Port
var channel = _channelFactory.CreateChannel(endpoint);
```

### F. Network Discovery (`Percolator.Application`)
`PeerDiscoveryHandler` creates `PeerIdentity` entries with `TrustState.Unknown` when peers are discovered via UDP. Trust is established entirely through the X3DH/Signal protocol layer, not TLS.

### G. Server-Side Configuration
In `MessageListenerService` (and the new `IGrpcServerManager`), configure Kestrel's HTTPS defaults:

```csharp
options.ConfigureHttpsDefaults(httpsOptions =>
{
    httpsOptions.ServerCertificate = provider.GetCertificate(identityContext.Identity);
    httpsOptions.SslProtocols = SslProtocols.Tls13;
    httpsOptions.ClientCertificateMode = ClientCertificateMode.NoCertificate; // No mTLS required
});
```

We do not require mTLS because:
1. X3DH provides end-to-end encryption and authentication
2. TLS 1.3 provides transport-layer encryption and privacy (certificate payload encryption)
3. Blind trust at the TLS layer is acceptable since the Signal protocol handles peer verification

### H. Application Bootstrap (Delayed On-Demand Host)
In `Desktop.Wpf`, Kestrel was previously attached globally to the application's `IHost` in `App.xaml.cs`. This causes a race condition because Kestrel starts before the user unlocks their identity. We will decouple the gRPC listener from the main host lifecycle using an On-Demand Host pattern with domain events and a background hosted service.

1. **Remove Kestrel from `App.xaml.cs`:** Strip `.ConfigureWebHostDefaults(...)` entirely. The global `IHost` will only manage WPF and application DI.
2. **Create `IGrpcServerManager` (`Percolator.Infrastructure`):**
```csharp
public interface IGrpcServerManager
{
    Task<ServerStartResult> StartAsync(SelfIdentity identity, CancellationToken ct);
    Task StopAsync(CancellationToken ct);
    Task RestartAsync(SelfIdentity identity, CancellationToken ct);
}

public class ServerStartResult
{
    public bool Success { get; }
    public string? ErrorMessage { get; }
    public bool IsPortConflict { get; }

    public static ServerStartResult Succeeded() => new() { Success = true };
    public static ServerStartResult Failed(string error, bool isPortConflict = false)
        => new() { Success = false, ErrorMessage = error, IsPortConflict = isPortConflict };
}
```

**Note:** Changed to accept `SelfIdentity` domain model instead of `ActiveIdentityContext` to avoid layering violations.
3. **Domain Event (`Percolator.Identity.DomainEvents`):**
```csharp
// Pure domain event - no MediatR dependency, carries only domain primitive
public class ActiveIdentityLoadedEvent : IDomainEvent
{
    public SelfId IdentityId { get; }
    public ActiveIdentityLoadedEvent(SelfId identityId) => IdentityId = identityId;
}
```

*MediatR Wrapper (`Percolator.Application.Messaging`):*
```csharp
public class DomainEventNotification<TDomainEvent> : INotification where TDomainEvent : IDomainEvent
{
    public TDomainEvent DomainEvent { get; }
    public DomainEventNotification(TDomainEvent domainEvent) => DomainEvent = domainEvent;
}
```
4. **Domain Event Handler (`Percolator.Infrastructure.Network`):**
```csharp
public class ActiveIdentityLoadedEventHandler
    : INotificationHandler<DomainEventNotification<ActiveIdentityLoadedEvent>>
{
    private readonly IGrpcServerManager _grpcServerManager;
    private readonly IIdentityNetworkService _networkService;
    private readonly ISelfIdentityRepository _identityRepository;
    private readonly IPublisher _publisher;
    private readonly ILogger<ActiveIdentityLoadedEventHandler> _logger;

    public ActiveIdentityLoadedEventHandler(
        IGrpcServerManager grpcServerManager,
        IIdentityNetworkService networkService,
        ISelfIdentityRepository identityRepository,
        IPublisher publisher,
        ILogger<ActiveIdentityLoadedEventHandler> logger)
    {
        _grpcServerManager = grpcServerManager;
        _networkService = networkService;
        _identityRepository = identityRepository;
        _publisher = publisher;
        _logger = logger;
    }

    public async Task Handle(DomainEventNotification<ActiveIdentityLoadedEvent> notification, CancellationToken ct)
    {
        var identityId = notification.DomainEvent.IdentityId;

        // Resolve the full identity from repository
        var identity = await _identityRepository.GetByIdAsync(identityId, ct);
        if (identity is null)
        {
            _logger.LogError("Identity {IdentityId} not found", identityId.Value);
            return;
        }

        // Stop existing server if running
        await _grpcServerManager.StopAsync(ct);

        var result = await _grpcServerManager.StartAsync(identity, ct);

        if (!result.Success && result.IsPortConflict)
        {
            await _networkService.ResolvePortContentionAsync(identityId, ct);

            // Reload identity with updated port
            var updatedIdentity = await _identityRepository.GetByIdAsync(identityId, ct);

            result = await _grpcServerManager.StartAsync(updatedIdentity, ct);

            if (!result.Success)
            {
                _logger.LogError(result.ErrorMessage, "Failed to start gRPC server after port reassignment");

                // Escalate fatal infrastructure failure to Application/Presentation layer
                await _publisher.Publish(new NodeOfflineNotification(identityId, result.ErrorMessage), ct);
            }
        }
    }
}
```

**Note:** Handler no longer creates `ActiveIdentityContext` - passes `SelfIdentity` directly to `IGrpcServerManager` to avoid layering violations.

5. **Node Offline Notification (`Percolator.Application`):**
```csharp
// Infrastructure failure notification - not a domain event
public class NodeOfflineNotification : INotification
{
    public SelfId IdentityId { get; }
    public string Reason { get; }
    public NodeOfflineNotification(SelfId identityId, string reason)
    {
        IdentityId = identityId;
        Reason = reason;
    }
}
```

**Note:** Changed to use `SelfId` domain type instead of `Guid` for consistency with domain primitives.

6. **Shutdown Hosted Service (`Percolator.Infrastructure.Network`):**
```csharp
public class GrpcShutdownHostedService : IHostedService
{
    private readonly IGrpcServerManager _grpcServerManager;
    
    public GrpcShutdownHostedService(IGrpcServerManager grpcServerManager)
    {
        _grpcServerManager = grpcServerManager;
    }
    
    public Task StartAsync(CancellationToken ct) => Task.CompletedTask;
    
    public async Task StopAsync(CancellationToken ct)
    {
        await _grpcServerManager.StopAsync(ct);
    }
}
```

*Application Layer Service (`Percolator.Application`):*
```csharp
public interface IIdentityNetworkService
{
    Task ResolvePortContentionAsync(SelfId identityId, CancellationToken ct);
}

public class IdentityNetworkService : IIdentityNetworkService
{
    private readonly INetworkEnvironment _networkEnvironment;
    private readonly ISelfIdentityRepository _identityRepository;

    public IdentityNetworkService(
        INetworkEnvironment networkEnvironment,
        ISelfIdentityRepository identityRepository)
    {
        _networkEnvironment = networkEnvironment;
        _identityRepository = identityRepository;
    }

    public async Task ResolvePortContentionAsync(SelfId identityId, CancellationToken ct)
    {
        var identity = await _identityRepository.GetByIdAsync(identityId, ct);
        if (identity is null)
        {
            throw new InvalidOperationException($"Identity {identityId.Value} not found");
        }
        var newPort = await _networkEnvironment.GetAvailablePortAsync(50000, 50100, ct);
        identity.UpdateListeningPort(newPort);
        await _identityRepository.SaveAsync(identity, ct);
    }
}
```

**Note:** Changed to use `SelfId` domain type and `SaveAsync` instead of `UpdateAsync` for consistency with repository interface.
8. **Dynamic Bootstrap:** The implementation of `IGrpcServerManager` will dynamically build a secondary `IWebHost` or `WebApplication` exclusively for the gRPC listener. It will retrieve the certificate from `ITransportCertificateProvider` and apply it to Kestrel via `listenOptions.UseHttps(cert)`. **Critical:** The secondary host must bridge gRPC service resolution to the primary WPF container. This is achieved by implementing the built-in `IGrpcServiceActivator<T>` from `Grpc.AspNetCore.Server` that creates a dedicated scope for each gRPC request to properly manage scoped dependencies.

```csharp
using Grpc.AspNetCore.Server;
using Microsoft.Extensions.DependencyInjection;

public class PrimaryContainerServiceActivator<T> : IGrpcServiceActivator<T> where T : class
{
    private readonly IServiceProvider _primaryProvider;

    public PrimaryContainerServiceActivator(IServiceProvider primaryProvider)
    {
        _primaryProvider = primaryProvider;
    }

    public GrpcActivatorHandle<T> Create(IServiceProvider serviceProvider)
    {
        // Create a dedicated scope for this specific gRPC request
        var scope = _primaryProvider.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<T>();

        // Pass the scope as state so it can be disposed later
        return new GrpcActivatorHandle<T>(service, created: true, state: scope);
    }

    public ValueTask ReleaseAsync(GrpcActivatorHandle<T> handle)
    {
        // Dispose the scope when the gRPC request completes
        if (handle.State is IServiceScope scope)
        {
            scope.Dispose();
        }
        return ValueTask.CompletedTask;
    }
}
```

Register it as an open generic in the secondary host:
```csharp
builder.Services.AddSingleton(typeof(IGrpcServiceActivator<>), typeof(PrimaryContainerServiceActivator<>));
```
9. **Triggered Startup:** In `Desktop.Wpf/Features/Self/IdentityStateService.cs`, inside `BootstrapAsync()` (immediately after the identity is fully loaded):
```csharp
var activeContext = new ActiveIdentityContext(identity);
var domainEvent = new ActiveIdentityLoadedEvent(activeContext);
var notification = new DomainEventNotification<ActiveIdentityLoadedEvent>(domainEvent);
await _publisher.Publish(notification, ct);
```
10. **Certificate Rotation:** Certificate rotation is handled synchronously during application bootstrap in `ITransportCertificateProvider.GetValidCertificateAsync`. The provider checks the certificate age and performs rotation before returning the certificate. This avoids circular dependencies between the provider and server manager. The rotation sequence:
   1. Check if file exists via `File.Exists(path)`
   2. If missing, immediately enter generation flow
   3. If exists, load the certificate and check `cert.NotBefore` (to calculate age) or `cert.NotAfter` (to check actual expiration)
   4. If older than 90 days, perform rotation:
      - Dispose existing X509Certificate2 instance
      - Generate new certificate
      - **Retry-backoff policy** around `File.WriteAllBytes` with exponential backoff (e.g., 5 retries with 100ms, 200ms, 400ms, 800ms, 1600ms delays) to handle antivirus scanning of newly written files
      - Load new certificate
   5. Return the valid certificate to Kestrel

This guarantees Kestrel never starts without cryptographic material, strictly isolates the network listener from the active identity, decouples identity state management from hosting lifecycle, safely handles port contention and certificate rotation, properly manages scoped dependencies across DI containers, and escalates fatal infrastructure failures to the UI.

### Conclusion
This design simplifies the TLS implementation by relying on X3DH/Signal for end-to-end security while using TLS 1.3 for transport-layer encryption and privacy. The key architectural changes are:
1. **Disk-based certificates on Windows:** Required for SChannel compatibility due to process isolation (LSASS cannot access in-memory ephemeral keys)
2. **ECDSA (P-256) certificates:** Forces Windows to use modern CNG KSP, avoiding legacy CSP issues with TLS 1.3
3. **Blind trust at TLS layer:** Accept any certificate; X3DH provides actual peer authentication
4. **No TOFU at TLS layer:** Trust is established through the Signal protocol, not certificate validation
5. **Persistent PFX files:** Written to Percolator directory (same as percolator.db) and managed by Infrastructure layer
6. **Certificate lifecycle:** Age-based rotation (default 90 days) handled by `ITransportCertificateProvider`
7. **Port assignment:** Application layer assigns ports via `INetworkEnvironment` during identity creation
8. **Static password:** Internal constant password for PFX files (no database storage)
9. **Domain events:** Decoupled identity loading from gRPC server startup via `ActiveIdentityLoadedEvent`
10. **Clean Architecture:** Domain layer has no knowledge of X509, PFX files, or Windows CNG

This approach works around Windows SChannel limitations while maintaining strong security through the Signal protocol layer and adhering to Clean Architecture principles.

---

## Part 3: Implementation Sequence

### Phase 1: Database Cleanup
1. Run EF Core migration to remove TLS certificate storage:
   ```powershell
   dotnet ef migrations add RemovePeerTlsCertificates --project Percolator.Infrastructure
   dotnet ef database update --project Percolator.Infrastructure
   ```
2. Delete obsolete code:
   - `Percolator.Network.ITrustedPeerStore`
   - `Percolator.Infrastructure.Network.FileBasedTrustedPeerStore`
   - `Percolator.Infrastructure.Network.Trust.InMemoryPeerTrustStore`
   - `Percolator.Infrastructure.Network.Tls.SharedCertificateManager`
   - `Percolator.Infrastructure.Cryptography.FileBasedCertificateFactory`
   - `Percolator.Infrastructure.Network.Tls.ITlsHandshakeService`
   - `Percolator.Infrastructure.Network.Tls.TlsHandshakeService`
3. Remove obsolete test files:
   - `Percolator.InfrastructureTests\Network\FileBasedTrustedPeerStoreTests.cs`
4. Note: `"percolator-grpc"` HttpClient registration is still used by `GrpcMessageTransportService` and was not removed

### Phase 2: Domain Layer Changes
1. Add `IDomainEvent` interface to `Percolator.Identity.SeedWork`
2. Add `ActiveIdentityLoadedEvent` to `Percolator.Identity.DomainEvents`
3. Add mutable `ListeningPort` property to `SelfIdentity` aggregate
4. Add `UpdateListeningPort` method to `SelfIdentity` aggregate
5. Add `ListeningPort` property to `IdentityRecord` (Application model)
6. Create EF Core migration to add `ListeningPort` column to `SelfIdentity` table

### Phase 3: Infrastructure Components
1. Implement `ITransportCertificateProvider` in `Percolator.Infrastructure.Network`
2. Implement `INetworkEnvironment` in `Percolator.Infrastructure.Network` (interface in Application, implementation in Infrastructure)
3. Implement `IPeerGrpcChannelFactory` in `Percolator.Infrastructure.Network`
4. Implement `IGrpcServerManager` in `Percolator.Infrastructure.Network`
5. Implement `PrimaryContainerServiceActivator<T>` in `Percolator.Infrastructure.Network`

### Phase 4: Application Layer
1. Create `DomainEventNotification<T>` wrapper in `Percolator.Application.Messaging`
2. Implement `IIdentityNetworkService` in `Percolator.Application`
3. Create `NodeOfflineNotification` in `Percolator.Application`
4. Update `CreateSelfIdentityCommandHandler` to use `INetworkEnvironment`
5. Update `IdentityStateService.BootstrapAsync` to publish `ActiveIdentityLoadedEvent`
6. Update `IdentityOrchestrator` to populate `ListeningPort` in `IdentityRecord`
7. Update `StartupIdentityService` to use `INetworkEnvironment` for port assignment

### Phase 5: Event Handlers
1. Implement `ActiveIdentityLoadedEventHandler` in `Percolator.Infrastructure.Network`
2. Implement `GrpcShutdownHostedService` in `Percolator.Infrastructure.Network`
3. Register handlers in DI container

### Phase 6: WPF Integration
1. Remove Kestrel configuration from `App.xaml.cs`
2. Register `GrpcShutdownHostedService` in WPF host
3. Add `NodeOfflineNotification` handler in WPF presentation layer for UI alerts

### Phase 7: Configuration
1. Add configuration section to `appsettings.json` (see Part 4)
2. Wire up configuration options in DI container

---

## Part 4: Configuration

### appsettings.json Structure
```json
{
  "Tls": {
    "PortRange": {
      "Start": 50000,
      "End": 50100
    },
    "Certificate": {
      "MaxAgeDays": 90,
      "Password": "percolator-node-transport"
    },
    "RetryPolicy": {
      "MaxRetries": 5,
      "InitialDelayMs": 100,
      "BackoffMultiplier": 2.0
    },
    "CleanupCertFiles": true
  }
}
```

### Configuration Options Class
```csharp
public class TlsOptions
{
    public PortRangeOptions PortRange { get; set; } = new();
    public CertificateOptions Certificate { get; set; } = new();
    public RetryPolicyOptions RetryPolicy { get; set; } = new();
    public bool CleanupCertFiles { get; set; } = true;
}

public class PortRangeOptions
{
    public int Start { get; set; } = 50000;
    public int End { get; set; } = 50100;
}

public class CertificateOptions
{
    public int MaxAgeDays { get; set; } = 90;
    public string Password { get; set; } = "percolator-node-transport";
}

public class RetryPolicyOptions
{
    public int MaxRetries { get; set; } = 5;
    public int InitialDelayMs { get; set; } = 100;
    public double BackoffMultiplier { get; set; } = 2.0;
}
```

### DI Registration
```csharp
builder.Services.Configure<TlsOptions>(builder.Configuration.GetSection("Tls"));
```

---

## Part 5: Certificate Disposal Strategy

### Disposal Responsibilities
1. **ITransportCertificateProvider**: Disposes certificate instances after rotation
2. **IGrpcServerManager.StopAsync**: Disposes certificate held by Kestrel before shutdown
3. **GrpcShutdownHostedService**: Ensures disposal on application exit

### Thread Safety
- Certificate instances are not shared across threads
- Each `GetValidCertificateAsync` call returns a new instance
- Kestrel holds a single instance per server lifecycle
- Disposal is synchronized via `StopAsync` which is called from a single thread (hosted service shutdown)

### Disposal Sequence on Rotation
Rotation happens during bootstrap inside `ITransportCertificateProvider.GetValidCertificateAsync` before the certificate is handed to Kestrel:
1. Load the existing certificate from disk to verify it
2. If rotation is required, call `Dispose()` on the old certificate instance
3. Generate new certificate
4. Write to disk with retry-backoff policy (for antivirus scanning)
5. Load new certificate
6. Return the valid certificate to `IGrpcServerManager.StartAsync()`

No calls to `StartAsync()` or `StopAsync()` occur during rotation - the orchestration belongs purely to the application bootstrap phase.

### Disposal Sequence on Application Shutdown
1. `GrpcShutdownHostedService.StopAsync()` is called by .NET host
2. Calls `IGrpcServerManager.StopAsync()` - disposes Kestrel's certificate
3. If `CleanupCertFiles` is true, delete PFX files after disposal
4. Application exits

---

## Part 6: Additional Implementation Details

### Configuration-Driven Port Range
The original plan specified `INetworkEnvironment.GetAvailablePortAsync(int startRange, int endRange, CancellationToken ct)` with parameters. During implementation, this was changed to use the configured port range from `TlsOptions.PortRange` instead:

**Updated Interface:**
```csharp
public interface INetworkEnvironment
{
    Task<int> GetAvailablePortAsync(CancellationToken ct);
}
```

**Implementation:**
- `NetworkEnvironment` now reads from `_tlsOptions.PortRange.Start` and `_tlsOptions.PortRange.End`
- All callers (`CreateSelfIdentityHandler`, `IdentityNetworkService`, `StartupIdentityService`) updated to remove hardcoded 50000/50100 parameters
- This makes the port range fully configurable via `appsettings.json`

### Certificate File Cleanup (Critical Architectural Decision)
**REJECTED:** The original plan mentioned a `CleanupCertFiles` configuration option for deleting PFX files on application shutdown. This was implemented but then **removed** due to critical architectural flaws:

**The Flaw: CNG Container Leak**
- Windows requires `X509KeyStorageFlags.PersistKeySet` for TLS 1.3 compatibility
- Every time a .pfx is generated and loaded with this flag, Windows creates a hidden cryptographic key container in OS AppData
- If certificates are deleted on shutdown and regenerated on next boot, orphaned OS key containers will leak every application cycle
- This eventually degrades system performance or hits Windows crypto limits

**The Secondary Flaw: Multi-Tenant Wipeout**
- A global cleanup hook that deletes all `tls_cert_*.pfx` files would destroy infrastructure for all identities on the machine
- This violates the isolation principle between different user identities

**The Fix:**
- Certificates **must persist** between application sessions to prevent CNG container leaks
- No cleanup hook exists in `GrpcShutdownHostedService`
- Certificates are only deleted when their parent identity is permanently deleted (not yet implemented - no UI exists)
- The `CleanupCertFiles` configuration option remains in `TlsOptions` but is unused and should be considered deprecated

---

## Part 7: GrpcSessionService "Dumb Pipe" Refactoring

### Critical Architectural Violations Identified

**1. Infrastructure Duplication (State Dictionaries)**
- **Flaw:** Service maintains `_channels`, `_httpClients`, and `_certificates` dictionaries
- **Impact:** Duplicates functionality of `IPeerGrpcChannelFactory`, creates duplicate HTTP/2 connections, exhausts OS resources, causes state synchronization bugs when peer IP changes
- **Fix:** Delete all state dictionaries and `CleanupConnectionResourcesAsync` method. Inject `IPeerGrpcChannelFactory` and let it own physical network state

**2. Local vs Remote Branching Violation**
- **Flaw:** Service manually parses IP address to check `isLocalConnection`. Creates unencrypted HTTP/2 for local, throws `NotImplementedException` for remote
- **Impact:** Violates physical transport abstraction. Domain shouldn't care about network topology. Kestrel server enforces TLS 1.3 everywhere, so unencrypted HTTP branch will fail to connect to own server
- **Fix:** Delete branching logic entirely. `IPeerGrpcChannelFactory` should return blind-trust TLS 1.3 channel for all endpoints (localhost or internet)

**3. Lingering TOFU and mTLS Artifacts**
- **Flaw:** Commented-out code attempts to inject `ClientCertificates`, enforce `CertificateRevocationCheckMode`, manually cache `X509Certificate2` instances
- **Impact:** Contradicts "Blind Trust" architecture where security is delegated to inner X3DH payload
- **Fix:** Delete all commented-out legacy TLS code and `NotImplementedException`. Transport layer is a dumb pipe

### Target Architecture: The "Dumb Pipe" Pattern

The service must act purely as a lightweight bridge between Domain session requests and the generated gRPC client. No state management, no local/remote branching, no TLS logic - all belongs in `IPeerGrpcChannelFactory`.

**Target Implementation:**
```csharp
using System.Net;
using Grpc.Core;
using Microsoft.Extensions.Logging;
using Percolator.Contracts;
using Percolator.Network.Services;

namespace Percolator.Infrastructure.Network.Grpc
{
    public class GrpcSessionService : ISessionEstablishmentTransport
    {
        private readonly IPeerGrpcChannelFactory _channelFactory;
        private readonly ILogger<GrpcSessionService> _logger;
        private readonly ISimulatorOutboundInterceptor? _simulatorOutbound;

        public GrpcSessionService(
            IPeerGrpcChannelFactory channelFactory,
            ILogger<GrpcSessionService> logger,
            ISimulatorOutboundInterceptor? simulatorOutbound = null)
        {
            _channelFactory = channelFactory;
            _logger = logger;
            _simulatorOutbound = simulatorOutbound;
        }

        public async Task<EstablishDirectSessionResponse> EstablishDirectSessionAsync(
            DnsEndPoint endpoint,
            EstablishDirectSessionRequest request)
        {
            if (_simulatorOutbound is not null &&
                _simulatorOutbound.TryEstablishDirectSession(endpoint, request, CancellationToken.None, out var simulated))
            {
                return await simulated.ConfigureAwait(false);
            }

            return await Inner_EstablishSession(endpoint, request).ConfigureAwait(false);
        }

        public async Task<EstablishSessionResponse> EstablishSessionAsync(
            DnsEndPoint endpoint,
            EstablishSessionRequest request,
            CancellationToken cancellationToken = default)
        {
            if (_simulatorOutbound is not null &&
                _simulatorOutbound.TryEstablishSession(endpoint, request, cancellationToken, out var simulated))
            {
                return await simulated.ConfigureAwait(false);
            }

            try
            {
                _logger.LogInformation("Sending EstablishSession request to {Endpoint}", endpoint);

                // 1. Get the physical pipe from the centralized factory
                var channel = _channelFactory.CreateChannel(new GrpcEndPoint(endpoint.Host, endpoint.Port));

                // 2. Instantiate the gRPC client
                var client = new TransportService.TransportServiceClient(channel);

                // 3. Make the call (Timeout logic remains here)
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                cts.CancelAfter(TimeSpan.FromSeconds(300));

                return await client.EstablishSessionAsync(request, cancellationToken: cts.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not RpcException)
            {
                _logger.LogError(ex, "Failed to send EstablishSession to {Endpoint}: {ErrorMessage}", endpoint, ex.Message);
                throw new RpcException(new Status(StatusCode.Unavailable, ex.Message));
            }
        }

        public async Task<DeliverInviteHandshakeResponseAck> DeliverInviteHandshakeResponseAsync(
            DnsEndPoint endpoint,
            InviteHandshakeResponse request)
        {
            if (_simulatorOutbound is not null &&
                _simulatorOutbound.TryDeliverInviteHandshakeResponse(endpoint, request, out var simulated))
            {
                return await simulated.ConfigureAwait(false);
            }

            try
            {
                _logger.LogInformation("Delivering InviteHandshakeResponse to {Endpoint}", endpoint);

                var channel = _channelFactory.CreateChannel(new GrpcEndPoint(endpoint.Host, endpoint.Port));
                var client = new TransportService.TransportServiceClient(channel);

                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(300));
                return await client.DeliverInviteHandshakeResponseAsync(request, cancellationToken: cts.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not RpcException)
            {
                _logger.LogError(ex, "Failed to deliver InviteHandshakeResponse to {Endpoint}: {ErrorMessage}", endpoint, ex.Message);
                throw new RpcException(new Status(StatusCode.Unavailable, ex.Message));
            }
        }

        private async Task<EstablishDirectSessionResponse> Inner_EstablishSession(
            DnsEndPoint endpoint,
            EstablishDirectSessionRequest request)
        {
            try
            {
                _logger.LogInformation("Establishing direct session with {Endpoint}", endpoint);

                var channel = _channelFactory.CreateChannel(new GrpcEndPoint(endpoint.Host, endpoint.Port));
                var client = new TransportService.TransportServiceClient(channel);

                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(300));
                return await client.EstablishDirectSessionAsync(request, cancellationToken: cts.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not RpcException)
            {
                _logger.LogError(ex, "Failed to establish direct gRPC session with {Endpoint}: {ErrorMessage}", endpoint, ex.Message);
                throw new RpcException(new Status(StatusCode.Unavailable, ex.Message));
            }
        }
    }
}
```

### Implementation Plan

#### Phase 1: Update IPeerGrpcChannelFactory for Blind Trust TLS

The factory must handle all TLS logic, including certificate management and blind trust validation.

**Current Interface:**
```csharp
public interface IPeerGrpcChannelFactory
{
    GrpcChannel CreateChannel(GrpcEndPoint endpoint);
}
```

**Updated Interface:**
```csharp
public interface IPeerGrpcChannelFactory
{
    GrpcChannel CreateChannel(GrpcEndPoint endpoint);
}
```

**No interface change needed** - the factory should internally handle certificate management.

**Implementation Changes in `PeerGrpcChannelFactory`:**
- Inject `ITransportCertificateProvider` to get the local identity's certificate
- Inject `ISelfIdentityRepository` to get the active identity
- Configure `SocketsHttpHandler` with TLS 1.3 for **all** endpoints (no local/remote distinction)
- Use blind trust for remote certificate validation (accept any certificate, security is in X3DH payload)
- Implement channel pooling internally (single channel per endpoint)
- Configure HTTP/2 ALPN, proper timeouts, keep-alive settings

**Certificate Configuration:**
```csharp
var activeIdentity = await _identityRepository.GetMostRecentAsync(ct);
var clientCertificate = await _certificateProvider.GetValidCertificateAsync(activeIdentity, ct);

handler = new SocketsHttpHandler
{
    SslOptions = new SslClientAuthenticationOptions
    {
        ClientCertificates = new X509CertificateCollection { clientCertificate },
        EnabledSslProtocols = System.Security.Authentication.SslProtocols.Tls12 | System.Security.Authentication.SslProtocols.Tls13,
        CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
        TargetHost = endpoint.Host,
        ApplicationProtocols = new List<SslApplicationProtocol> { SslApplicationProtocol.Http2 },
        RemoteCertificateValidationCallback = (sender, cert, chain, errors) => true // Blind trust
    },
    PooledConnectionIdleTimeout = TimeSpan.FromMinutes(5),
    KeepAlivePingTimeout = TimeSpan.FromSeconds(20),
    KeepAlivePingDelay = TimeSpan.FromSeconds(60),
    EnableMultipleHttp2Connections = true,
    ConnectTimeout = TimeSpan.FromSeconds(10)
};
```

**Channel Pooling:**
- Maintain `ConcurrentDictionary<string, GrpcChannel>` internally keyed by endpoint
- Return existing channel if available, create new if not
- Handle channel lifecycle (dispose on application shutdown via `IHostedService`)

#### Phase 2: Refactor GrpcSessionService

**Delete:**
- `_channels` dictionary
- `_httpClients` dictionary
- `_certificates` dictionary
- `CleanupConnectionResourcesAsync` method
- All local/remote branching logic (`isLocalConnection` checks)
- All `SocketsHttpHandler` creation code
- All `HttpClient` creation code
- All commented-out legacy TLS code
- `NotImplementedException` throws
- TODO comments

**Add:**
- Inject `IPeerGrpcChannelFactory` in constructor
- Use factory for all channel creation: `_channelFactory.CreateChannel(new GrpcEndPoint(endpoint.Host, endpoint.Port))`
- Keep simulator interceptor (testing concern, acceptable)
- Keep timeout logic (application-level concern)

**Constructor:**
```csharp
public GrpcSessionService(
    IPeerGrpcChannelFactory channelFactory,
    ILogger<GrpcSessionService> logger,
    ISimulatorOutboundInterceptor? simulatorOutbound = null)
{
    _channelFactory = channelFactory;
    _logger = logger;
    _simulatorOutbound = simulatorOutbound;
}
```

#### Phase 3: Update IPeerGrpcChannelFactory Lifecycle

Since the factory now owns channel state, it needs proper lifecycle management.

**Add to `PeerGrpcChannelFactory`:**
```csharp
public async Task ShutdownAsync()
{
    foreach (var channel in _channels.Values)
    {
        try
        {
            await channel.ShutdownAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error shutting down channel");
        }
    }
    _channels.Clear();
}
```

**Create `GrpcChannelShutdownHostedService`:**
```csharp
public class GrpcChannelShutdownHostedService : IHostedService
{
    private readonly IPeerGrpcChannelFactory _channelFactory;

    public GrpcChannelShutdownHostedService(IPeerGrpcChannelFactory channelFactory)
    {
        _channelFactory = channelFactory;
    }

    public Task StartAsync(CancellationToken ct) => Task.CompletedTask;

    public async Task StopAsync(CancellationToken ct)
    {
        await _channelFactory.ShutdownAsync();
    }
}
```

**Register in DI:**
```csharp
services.AddHostedService<GrpcChannelShutdownHostedService>();
```

#### Phase 4: Configuration

**No new configuration needed** - existing `TlsOptions` is sufficient. The factory uses the same certificate provider and settings as the local server.

**Note:** Remove `RemoteTlsOptions` from the plan (not needed for dumb pipe architecture).

#### Phase 5: Dependency Injection Registration

**Update ServiceCollectionExtensions:**
```csharp
public static IServiceCollection AddNetworkInfrastructure(this IServiceCollection services)
{
    // Existing registrations...
    services.AddScoped<IPeerGrpcChannelFactory, PeerGrpcChannelFactory>();
    services.AddHostedService<GrpcChannelShutdownHostedService>();
    services.AddScoped<GrpcSessionService>();
    return services;
}
```

#### Phase 6: Testing Considerations

**Unit Tests:**
- Mock `IPeerGrpcChannelFactory` to return mock channels
- Test that GrpcSessionService delegates to factory correctly
- Test simulator interceptor integration
- Test timeout logic

**Integration Tests:**
- Test actual TLS handshake with real certificates
- Test channel pooling (reuse existing channel for same endpoint)
- Test blind trust certificate validation
- Test localhost connections (should use TLS, not HTTP)

### Summary of Changes

**New Files:**
- `Percolator.Infrastructure.Network.GrpcChannelShutdownHostedService` - manages factory lifecycle

**Modified Files:**
- `Percolator.Infrastructure.Network.IPeerGrpcChannelFactory` - no interface change, but implementation now handles TLS
- `Percolator.Infrastructure.Network.PeerGrpcChannelFactory` - implement TLS channel creation, channel pooling, blind trust
- `Percolator.Infrastructure.Network.Grpc.GrpcSessionService` - gut to dumb pipe, remove all state and branching logic
- `Percolator.Infrastructure.ServiceCollectionExtensions` - register shutdown service

**Deleted Code:**
- All state dictionaries from GrpcSessionService (`_channels`, `_httpClients`, `_certificates`)
- `CleanupConnectionResourcesAsync` method
- All local/remote branching logic
- All commented-out legacy TLS code
- All `SocketsHttpHandler` and `HttpClient` creation code
- All TODO comments
- `NotImplementedException` throws

**No Database Migration Needed** - No trust store required for blind trust architecture.

---

## Part 8: Callsite Updates and Unit Testing

### Callsites Requiring Updates

**Test Code Callsites:**

5. **Percolator.Node\**
    - Console application using legacy `SharedCertificateManager` and `Tls` namespace
    - **Action:** Separate project - requires its own migration plan (see TODO #101)



### Notes

- The existing `SimulatorOutboundInterceptionTests.cs` already tests the interceptor integration pattern and should be updated to reflect the new CancellationToken parameter
- The test doubles in integration tests (`SingleHostGrpcSessionLoopback`) are minimal implementations and should simply add the CancellationToken parameter without changing behavior
- No integration tests are suggested for the actual TLS handshake behavior - this is covered by the `TofuTlsGrpcTest` console application which performs end-to-end testing with real certificates 

---

## Phase 9: Simulator Localhost gRPC Client Refactoring (Option 1)

### Objective
The `SimulatorToMainTransportService` is currently bridging the simulator to the main application using fragile DI-based in-memory invocations (`_primaryProvider.GetRequiredService<PercolatorMessageService>()`). This bypasses the new Kestrel gRPC server pipeline, missing interceptors (like `IdentityReadinessInterceptor`) and breaking DI scoping. 

To correctly exercise the real application paths and prepare the Simulator to be extracted into an independent application, we must refactor this service to make **real localhost gRPC calls** over loopback, hitting the actual listening port of the main application.

### Implementation Steps

#### 1. Extract Channel Management (Clean Architecture)
Create a new interface and implementation for managing the gRPC channel lifecycle. This prevents socket exhaustion and adheres to Dependency Inversion.

**File:** `Desktop.Wpf\Features\Simulator\ISimulatorGrpcClientFactory.cs`
```csharp
public interface ISimulatorGrpcClientFactory
{
    TransportService.TransportServiceClient CreateClient();
}
```

**File:** `Desktop.Wpf\Features\Simulator\SimulatorGrpcClientFactory.cs`
- Inject `ActiveIdentityContext`.
- Maintain a cached `GrpcChannel`. 
- Recreate the channel *only* if the `ActiveIdentityContext.Identity.ListeningPort` changes.
- Implement `IDisposable` to dispose of the `GrpcChannel` and `HttpClientHandler`.
- Configure the `HttpClientHandler` with `DangerousAcceptAnyServerCertificateValidator` since the simulator must blind-trust the ephemeral localhost cert.

#### 2. Update Transport Service Dependencies
Refactor the constructor of `Desktop.Wpf.Features.Simulator.SimulatorToMainTransportService`:
- Remove `IServiceProvider _primaryProvider`.
- Inject `ISimulatorGrpcClientFactory _clientFactory`.

#### 3. Refactor Interface Methods
Refactor all four interface methods to use the real generated client.

Example for `SendOpaqueMessageToMainAsync`:
```csharp
public async Task<DeliverOpaqueMessageResponse> SendOpaqueMessageToMainAsync(
    DeliverOpaqueMessageRequest request,
    CancellationToken cancellationToken = default)
{
    if (request is null) throw new ArgumentNullException(nameof(request));
    
    var client = _clientFactory.CreateClient();
    return await client.DeliverOpaqueMessageAsync(request, cancellationToken: cancellationToken).ConfigureAwait(false);
}
```

#### 4. Explicit Dead Code Cleanup
- **In `SimulatorMainIngressService.cs`:** Completely delete the `ServerCallContextStub` nested class.
- **In `SimulatorMainIngressService.cs`:** Remove unused using directives: `using Grpc.Core;`, `using Microsoft.Extensions.DependencyInjection;`, `using Percolator.Infrastructure.Network.Grpc;`.
- **In `App.xaml.cs`:** Register the new factory: `services.AddSingleton<ISimulatorGrpcClientFactory, SimulatorGrpcClientFactory>();`. Update the registration for `SimulatorToMainTransportService` if necessary.

#### 5. Unit Testing Strategy (Adhering to unit-testing.md)
Create `SimulatorToMainTransportServiceTests.cs` following the AAA pattern and Black Box Rule.

- **Mock the Boundary:** Use Moq to mock `ISimulatorGrpcClientFactory` and the generated `TransportService.TransportServiceClient` (gRPC client methods are virtual and mockable).
- **Test Behavior:** Verify that calling `SendOpaqueMessageToMainAsync` successfully delegates to the gRPC client without throwing, and handles cancellation tokens correctly.

**Example Test:**
```csharp
[Test]
public async Task SendOpaqueMessageToMainAsync_GivenValidRequest_InvokesGrpcClient()
{
    // ARRANGE
    var request = new DeliverOpaqueMessageRequest { Version = 1 };
    var expectedResponse = new DeliverOpaqueMessageResponse { Version = 1 };
    
    var clientMock = new Mock<TransportService.TransportServiceClient>();
    clientMock.Setup(c => c.DeliverOpaqueMessageAsync(
        request, null, null, It.IsAny<CancellationToken>()))
        .Returns(new AsyncUnaryCall<DeliverOpaqueMessageResponse>(
            Task.FromResult(expectedResponse),
            Task.FromResult(new Metadata()),
            () => Status.DefaultSuccess,
            () => new Metadata(),
            () => { }));

    var factoryMock = new Mock<ISimulatorGrpcClientFactory>();
    factoryMock.Setup(f => f.CreateClient()).Returns(clientMock.Object);

    var sut = new SimulatorToMainTransportService(factoryMock.Object);

    // ACT
    var result = await sut.SendOpaqueMessageToMainAsync(request, CancellationToken.None);

    // ASSERT
    result.Should().BeEquivalentTo(expectedResponse);
    clientMock.Verify(c => c.DeliverOpaqueMessageAsync(request, null, null, It.IsAny<CancellationToken>()), Times.Once);
}
``` 

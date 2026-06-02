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

### C. Network Environment (`Percolator.Infrastructure`)
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
    Task<ServerStartResult> StartAsync(ActiveIdentityContext identityContext, CancellationToken ct);
    Task StopAsync(CancellationToken ct);
    Task RestartAsync(ActiveIdentityContext identityContext, CancellationToken ct);
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
3. **Domain Event (`Percolator.Identity.DomainEvents`):**
```csharp
// Pure domain event - no MediatR dependency
public class ActiveIdentityLoadedEvent : IDomainEvent
{
    public ActiveIdentityContext IdentityContext { get; }
    public ActiveIdentityLoadedEvent(ActiveIdentityContext identityContext) => IdentityContext = identityContext;
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
        var context = notification.DomainEvent.IdentityContext;
        
        // Stop existing server if running
        await _grpcServerManager.StopAsync(ct);
        
        var result = await _grpcServerManager.StartAsync(context, ct);
        
        if (!result.Success && result.IsPortConflict)
        {
            await _networkService.ResolvePortContentionAsync(context.Identity.Id, ct);
            
            // Create new context with updated identity (immutable event payload)
            var updatedIdentity = await _identityRepository.GetByIdAsync(context.Identity.Id);
            var updatedContext = new ActiveIdentityContext(updatedIdentity);
            
            result = await _grpcServerManager.StartAsync(updatedContext, ct);
            
            if (!result.Success)
            {
                _logger.LogError(result.ErrorMessage, "Failed to start gRPC server after port reassignment");
                
                // Escalate fatal infrastructure failure to Application/Presentation layer
                await _publisher.Publish(new NodeOfflineNotification(context.Identity.Id, result.ErrorMessage), ct);
            }
        }
    }
}
```

5. **Node Offline Notification (`Percolator.Application`):**
```csharp
// Infrastructure failure notification - not a domain event
public class NodeOfflineNotification : INotification
{
    public Guid IdentityId { get; }
    public string Reason { get; }
    public NodeOfflineNotification(Guid identityId, string reason)
    {
        IdentityId = identityId;
        Reason = reason;
    }
}
```

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
    Task ResolvePortContentionAsync(Guid identityId, CancellationToken ct);
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
    
    public async Task ResolvePortContentionAsync(Guid identityId, CancellationToken ct)
    {
        var identity = await _identityRepository.GetByIdAsync(identityId);
        var newPort = await _networkEnvironment.GetAvailablePortAsync(50000, 50100, ct);
        identity.UpdateListeningPort(newPort);
        await _identityRepository.UpdateAsync(identity);
    }
}
```
8. **Dynamic Bootstrap:** The implementation of `IGrpcServerManager` will dynamically build a secondary `IWebHost` or `WebApplication` exclusively for the gRPC listener. It will retrieve the certificate from `ITransportCertificateProvider` and apply it to Kestrel via `listenOptions.UseHttps(cert)`. **Critical:** The secondary host must bridge gRPC service resolution to the primary WPF container. This is achieved by implementing a generic `IGrpcServiceActivator<T>` that creates a dedicated scope for each gRPC request to properly manage scoped dependencies.

```csharp
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
3. Remove generic `"percolator-grpc"` HttpClient registration from `ServiceCollectionExtensions.cs`

### Phase 2: Domain Layer Changes
1. Add `IDomainEvent` interface to `Percolator.Identity.SeedWork`
2. Add `ActiveIdentityLoadedEvent` to `Percolator.Identity.DomainEvents`
3. Add mutable `ListeningPort` property to `SelfIdentity` aggregate
4. Add `UpdateListeningPort` method to `SelfIdentity` aggregate

### Phase 3: Infrastructure Components
1. Implement `ITransportCertificateProvider` in `Percolator.Infrastructure.Network`
2. Implement `INetworkEnvironment` in `Percolator.Infrastructure.Network`
3. Implement `IPeerGrpcChannelFactory` in `Percolator.Infrastructure.Network`
4. Implement `IGrpcServerManager` in `Percolator.Infrastructure.Network`
5. Implement `PrimaryContainerServiceActivator<T>` in `Percolator.Infrastructure.Network`

### Phase 4: Application Layer
1. Create `DomainEventNotification<T>` wrapper in `Percolator.Application.Messaging`
2. Implement `IIdentityNetworkService` in `Percolator.Application`
3. Create `NodeOfflineNotification` in `Percolator.Application`
4. Update `CreateSelfIdentityCommandHandler` to use `INetworkEnvironment`
5. Update `IdentityStateService.BootstrapAsync` to publish `ActiveIdentityLoadedEvent`

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

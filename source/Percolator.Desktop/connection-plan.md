# Desktop & Simulator Connection Architecture — Implementation Plan

Spec: [implementation-plan-spec](../spec/implementation-plan.spec.md) — read this spec before modifying this document.

⬜ not started · 🚧 in progress · ✅ done

Verification convention: Any ticket modifying simulation or test-harness hosts must execute `dotnet run --project Percolator.Simulator --framework net10.0-windows10.0.19041.0 -- --replay Scenarios` before completion.

> [!IMPORTANT]
> **Zero Data Migration Policy**: There is zero requirement for data migration or backward schema compatibility for existing SQLite files. All local databases are transient development and testing environments. When schema or entity adjustments occur, databases may be cleanly recreated with zero migration scaffolding or version fallback code.

## Refinement Status Gate
- [x] Lens 1: Scope & Feature Slice Framing (Status: Approved)
- [x] Lens 2: Onion Architecture & Primitive Obsession (Status: Approved)
- [x] Lens 3: Threat Modeling & Security Posture (Status: Approved)
- [x] Lens 4: High-Value, Low-Brittle Unit Test Design (Status: Approved)
- [x] Lens 5: Cross-Layer Infrastructure Alignment (Status: Approved)

Implementation Gate: UNLOCKED

---

## Architecture

| Layer | Type | Location | Lifetime |
| :--- | :--- | :--- | :--- |
| Domain | `PortRange` | `Percolator.Domain/Identities/ValueObjects/PortRange.cs` | Transient (Value Object) |
| Application | `PeerInvitationPayload` | `Percolator.Application/Invitations/PeerInvitationPayload.cs` | Transient (Record) |
| Application | `IPeerInvitationCodec` | `Percolator.Application/Invitations/IPeerInvitationCodec.cs` | Singleton |
| Application | `PeerInvitationCodec` | `Percolator.Application/Invitations/PeerInvitationCodec.cs` | Singleton |
| Application | `IPeerConnectionService` | `Percolator.PluginSdk/IPeerConnectionService.cs` | Scoped |
| Application | `PeerConnectionService` | `Percolator.Application/Services/PeerConnectionService.cs` | Scoped |
| Infrastructure | `PeerTransportServerOptions` | `Percolator.Infrastructure/Transport/PeerTransportServerOptions.cs` | Singleton |
| Infrastructure | `TransportCertificateSanBuilder` | `Percolator.Infrastructure/Transport/TransportCertificateSanBuilder.cs` | Static (Internal Helper) |
| Simulator | `ISimulatorChaosController` | `Percolator.Simulator/Features/Chaos/ISimulatorChaosController.cs` | Singleton |
| Simulator | `SimulatorChaosController` | `Percolator.Simulator/Features/Chaos/SimulatorChaosController.cs` | Singleton |
| Simulator | `ChaosRelayMailboxCapabilityStoreDecorator` | `Percolator.Simulator/Features/Chaos/ChaosRelayMailboxCapabilityStoreDecorator.cs` | Scoped / Singleton |
| Simulator | `ChaosPendingIngressStoreDecorator` | `Percolator.Simulator/Features/Chaos/ChaosPendingIngressStoreDecorator.cs` | Scoped |
| Desktop | `AddConnectionDialogViewModel` | `Percolator.Desktop/Features/Transports/ViewModels/AddConnectionDialogViewModel.cs` | Transient |
| Desktop | `OutboundPendingConnectionRetryWorker` | `Percolator.Desktop/Features/Transports/Services/OutboundPendingConnectionRetryWorker.cs` | Singleton (Hosted Service) |
| Desktop | `PendingRequestsPanelViewModel` | `Percolator.Desktop/Features/Transports/ViewModels/PendingRequestsPanelViewModel.cs` | Transient |
| Desktop | `ShareInvitationDialogViewModel` | `Percolator.Desktop/Features/Transports/ViewModels/ShareInvitationDialogViewModel.cs` | Transient |

---

## Tickets

### CONN-01 ⬛ Multi-Interface Network Listener Binding (`0.0.0.0` / `IPAddress.Any` - IPv4 Only)

- **IPv4 Mandate:** Sockets bind strictly to IPv4 `IPAddress.Any` (`0.0.0.0`). Dual-stack IPv6 binding is explicitly excluded to preserve route predictability and avoid unauthenticated IPv6 metadata leakage.

- **Files:**
  - `Create`: `Percolator.Infrastructure/Transport/PeerTransportServerOptions.cs`
  - `Modify`: `Percolator.Infrastructure/Transport/KestrelPeerTransportServer.cs` (~L20, inject `PeerTransportServerOptions`; ~L54, default `bindAddress` to `_options.BindAddress`; ~L157 in `IHostedService.StartAsync`, pass `_options.BindAddress`)
  - `Modify`: `Percolator.Infrastructure/DependencyInjection/InfrastructureServiceCollectionExtensions.cs` (~L239, register `PeerTransportServerOptions`; ~L387, configure `CallbackEndpointValidatorOptions` with `AllowLan = true` and `AllowLoopback = true`)
  - `Modify`: `Percolator.Desktop/Common/Services/ExchangeDirectoryService.cs` (~L220, replace `IPAddress.Loopback` with `IPAddress.Any` when binding exchange listener)
  - `Modify`: `Percolator.Simulator/Features/RelayedChat/SimulatedRelayHost.cs` (~L197, replace `options.Listen(IPAddress.Loopback, BoundPort, ...)` with `options.Listen(IPAddress.Any, BoundPort, ...)`)
  - `Modify`: `Percolator.Simulator/Features/Topology/SimulatedPersonaHost.cs` (~L323 and ~L772, replace `_transportServer.StartAsync(IPAddress.Loopback, ...)` with `_transportServer.StartAsync(IPAddress.Any, ...)`)

- **Contracts & DI:**
  ```csharp
  namespace Percolator.Infrastructure.Transport;

  public sealed class PeerTransportServerOptions
  {
      public IPAddress BindAddress { get; set; } = IPAddress.Any;
  }
  ```
  ```csharp
  services.AddSingleton<PeerTransportServerOptions>();
  services.AddSingleton<KestrelPeerTransportServer>();
  services.AddSingleton<IPeerTransportServer>(sp => sp.GetRequiredService<KestrelPeerTransportServer>());
  services.AddSingleton<IHostedService>(sp => sp.GetRequiredService<KestrelPeerTransportServer>());
  services.AddSingleton(new CallbackEndpointValidatorOptions { AllowLan = true, AllowLoopback = true });
  services.AddSingleton<CallbackEndpointValidator>();
  ```

- **Algorithm:**
  1. `KestrelPeerTransportServer`: inject `PeerTransportServerOptions`. In `IHostedService.StartAsync`, invoke `StartAsync(_options.BindAddress, ct: cancellationToken)`.
  2. `SimulatedRelayHost.StartAsync`: configure Kestrel listener to `options.Listen(IPAddress.Any, BoundPort, listenOptions => ...)`.
  3. `SimulatedPersonaHost.StartAsync`: invoke `await _transportServer.StartAsync(IPAddress.Any, new PortNumber(BoundPort), ct: ct)`.
  4. Sockets bind only when visual nodes are activated; zero open sockets prior to explicit start.

- **Verify:**
  - `Percolator.Infrastructure.Tests/Transport/KestrelPeerTransportServerTests.cs`
  - Command: `dotnet test Percolator.Infrastructure.Tests --filter "FullyQualifiedName~KestrelPeerTransportServer"`
  - Command: `dotnet run --project Percolator.Simulator --framework net10.0-windows10.0.19041.0 -- --replay Scenarios`

- **QMS:** [`DEP-DESKTOP-PRE-ACTIVATION-STEALTH`], [`INV-DESKTOP-STEALTH-ACTIVATION-GATE`], [`SYS-PRE-ACTIVATION-STEALTH`]

---

### CONN-02 ⬛ Dynamic Routable LAN Endpoint Resolution & Certificate SAN Expansion

- **Files:**
  - `Create`: `Percolator.Infrastructure/Transport/TransportCertificateSanBuilder.cs`
  - `Modify`: `Percolator.Infrastructure/Transport/InMemoryTransportCertificateProvider.cs` (~L76-80, delegate SAN population to `TransportCertificateSanBuilder.PopulateSan(sanBuilder)`)
  - `Modify`: `Percolator.Infrastructure.Windows/Security/WindowsSChannelCertificateProvider.cs` (~L116-120, delegate SAN population to `TransportCertificateSanBuilder.PopulateSan(sanBuilder)`)
  - `Modify`: `Percolator.Simulator/Features/Topology/SimulatedPersonaHost.cs` (~L87, replace hardcoded loopback endpoint in `BoundEndpoint` with dynamic candidate lookup)
  - `Modify`: `Percolator.Simulator/Features/RelayedChat/SimulatedRelayHost.cs` (~L75, replace hardcoded loopback endpoint in `BoundEndpoint` with dynamic candidate lookup)

- **Contracts & Code Blocks:**
  ```csharp
  namespace Percolator.Infrastructure.Transport;

  internal static class TransportCertificateSanBuilder
  {
      public static void PopulateSan(SubjectAlternativeNameBuilder builder)
      {
          builder.AddDnsName("localhost");
          builder.AddIpAddress(IPAddress.Loopback);
          builder.AddIpAddress(IPAddress.IPv6Loopback);
          try
          {
              builder.AddDnsName(Dns.GetHostName());
              var interfaces = NetworkInterface.GetAllNetworkInterfaces();
              foreach (var ni in interfaces)
              {
                  if (ni.OperationalStatus != OperationalStatus.Up ||
                      ni.NetworkInterfaceType == NetworkInterfaceType.Loopback ||
                      ni.NetworkInterfaceType == NetworkInterfaceType.Tunnel)
                  {
                      continue;
                  }
                  foreach (var uni in ni.GetIPProperties().UnicastAddresses)
                  {
                      if (uni.Address.AddressFamily is AddressFamily.InterNetwork or AddressFamily.InterNetworkV6)
                      {
                          builder.AddIpAddress(uni.Address);
                      }
                  }
              }
          }
          catch (Exception) when (!OperatingSystem.IsWindows())
          {
              // Best-effort network enumeration fallback on sandboxed POSIX environments
          }
      }
  }
  ```

- **Algorithm:**
  1. In `SimulatedPersonaHost` and `SimulatedRelayHost`, replace `BoundEndpoint` getter:
     ```csharp
     public Uri BoundEndpoint => ResolveRoutableEndpoint();

     private Uri ResolveRoutableEndpoint()
     {
         var discovery = new NetworkInterfaceEndpointDiscoveryPort(BoundPort, allowLoopback: true);
         var candidates = discovery.DiscoverLocalCandidatesAsync(DirectWanPolicy.DirectLanOnly).AsTask().GetAwaiter().GetResult();
         var primaryLan = candidates.FirstOrDefault(c => c.Kind == DirectCandidateKind.DirectLan && !IPAddress.IsLoopback(IPAddress.Parse(c.Host.Value)))
                          ?? candidates.FirstOrDefault();
         var host = primaryLan?.Host.Value ?? "127.0.0.1";
         return new Uri($"https://{host}:{BoundPort}");
     }
     ```
  2. `InMemoryTransportCertificateProvider` and `WindowsSChannelCertificateProvider` invoke `TransportCertificateSanBuilder.PopulateSan(sanBuilder)` during certificate generation, including all host network addresses in SAN extensions.

- **Verify:**
  - `Percolator.Infrastructure.Tests/Transport/Primitives/InMemoryTransportCertificateProviderTests.cs`
  - `Percolator.Infrastructure.Windows.Tests/Security/WindowsSChannelCertificateProviderTests.cs`
  - Command: `dotnet test Percolator.Infrastructure.Tests --filter "FullyQualifiedName~CertificateProvider"`
  - Command: `dotnet run --project Percolator.Simulator --framework net10.0-windows10.0.19041.0 -- --replay Scenarios`

- **QMS:** [`SYS-MUTUAL-AUTHENTICATED-WIRE-TRANSPORT`], [`HAZ-TRANSPORT-SPOOFING`]

---

### CONN-03 ⬛ Out-of-Band Invitation Codec & Port Range Settings

- **Zero Data Migration Mandate:** Existing SQLite files will not be migrated or preserved. Recreating tables or resetting database files is expected. Zero EF migration scripts or backwards-compatibility shims are permitted.

- **Files:**
  - `Create`: `Percolator.Domain/Identities/ValueObjects/PortRange.cs`
  - `Modify`: `Percolator.Application/Ports/IVaultSettingsStore.cs` (~L12, extend `IdentityLaunchSettings` record with optional `PortRangeStart` and `PortRangeEnd`)
  - `Modify`: `Percolator.Infrastructure/Persistence/Entities/VaultSettingsEntities.cs` (~L14, add `PortRangeStart` and `PortRangeEnd` nullable columns to `IdentitySettingsEntity`)
  - `Modify`: `Percolator.Infrastructure/Persistence/Repositories/SqliteVaultSettingsStore.cs` (~L75 and ~L125, persist and map `PortRangeStart` and `PortRangeEnd`)
  - `Create`: `Percolator.Application/Invitations/PeerInvitationPayload.cs`
  - `Create`: `Percolator.Application/Invitations/IPeerInvitationCodec.cs`
  - `Create`: `Percolator.Application/Invitations/PeerInvitationCodec.cs`
  - `Modify`: `Percolator.Infrastructure/DependencyInjection/InfrastructureServiceCollectionExtensions.cs` (register `IPeerInvitationCodec` as Singleton)

- **Contracts:**
  ```csharp
  namespace Percolator.Domain.Identities.ValueObjects;

  public sealed record PortRange
  {
      public int MinPort { get; }
      public int MaxPort { get; }

      public PortRange(int minPort, int maxPort)
      {
          if (minPort < 1 || minPort > 65535) throw new ArgumentOutOfRangeException(nameof(minPort), minPort, "Port out of range.");
          if (maxPort < 1 || maxPort > 65535) throw new ArgumentOutOfRangeException(nameof(maxPort), maxPort, "Port out of range.");
          if (minPort > maxPort) throw new ArgumentException("MinPort must be less than or equal to MaxPort.", nameof(minPort));
          MinPort = minPort;
          MaxPort = maxPort;
      }

      public static PortRange DefaultDynamicRange => new(5100, 5299);
      public static PortRange Single(int port) => new(port, port);
  }
  ```

  ```csharp
  namespace Percolator.Application.Invitations;

  public sealed record PeerInvitationPayload(
      PublicIdentityId IdentityId,
      BlindedRoutingToken RoutingToken,
      DeliveryToken DeliveryToken,
      IReadOnlyList<string> EndpointHosts,
      PortRange? PortRange);

  public interface IPeerInvitationCodec
  {
      string Encode(PeerInvitationPayload payload);
      DomainResult<PeerInvitationPayload> Decode(string invitationString);
  }
  ```

- **Algorithm:**
  1. `PeerInvitationCodec.Decode`:
     - Inspects input string; if prefixed with `percolator://`, silently strips the protocol prefix.
     - If formatted as URL (`invite?id=...&route=...&deliv=...&endpoints=...&ports=...`), parses query parameters:
       - `id`: parsed as `PublicIdentityId`.
       - `route`: parsed from hex as `BlindedRoutingToken`.
       - `deliv`: parsed from hex as `DeliveryToken`.
       - `endpoints`: split by comma into host list.
       - `ports`: if present as `min-max`, parses `PortRange(min, max)`; if single number, parses `PortRange.Single(p)`; if omitted, defaults to `PortRange.DefaultDynamicRange`.
     - If formatted as base64 blob, deserializes compact binary payload containing the same fields.
  2. `IdentityLaunchSettings` updated:
     ```csharp
     public sealed record IdentityLaunchSettings(
         int PreferredPort,
         int? LastBoundPort,
         bool AutoStart,
         int SidebarOrder,
         int? PortRangeStart = null,
         int? PortRangeEnd = null);
     ```

- **Verify:**
  - `Percolator.Application.Tests/Invitations/PeerInvitationCodecTests.cs`
  - `Percolator.Infrastructure.Tests/Persistence/Vault/SqliteVaultSettingsStoreTests.cs`
  - Command: `dotnet test Percolator.Application.Tests --filter "FullyQualifiedName~PeerInvitationCodec"`
  - Command: `dotnet test Percolator.Infrastructure.Tests --filter "FullyQualifiedName~SqliteVaultSettingsStoreTests"`

- **QMS:** [`DEP-DESKTOP-OUT-OF-BAND-VERIFICATION`], [`SYS-OUT-OF-BAND-SECURITY-CODE`]

---

### CONN-04 ⬛ Eliminate Direct Handshake Duplication & Anti-Shortcut Cross-Host Peeking

- **Files:**
  - `Modify`: `Percolator.PluginSdk/IPeerConnectionService.cs` (~L23, add overload/method `ConnectToDirectPeerAsync`)
  - `Modify`: `Percolator.Application/Services/PeerConnectionService.cs` (~L42, implement `ConnectToDirectPeerAsync` saving routing capability and stream registry before prekey fetch)
  - `Modify`: `Percolator.Simulator/Features/DirectChat/DirectHandshakeOrchestrator.cs` (~L97-178, resolve `IPeerConnectionService` from initiator scope and invoke `ConnectToDirectPeerAsync`; ~L308-335, delete `EnsureChannelAsync` cross-host repository write)
  - `Modify`: `Percolator.Infrastructure/Adapters/PreKeyBundleProviderAdapter.cs` (~L64-101, extract target endpoint from `relayEndpoint` or `capability.RelayEndpoint` for gRPC channel resolution)

- **Contracts:**
  ```csharp
  namespace Percolator.PluginSdk;

  public interface IPeerConnectionService
  {
      Task<DomainResult<PeerConnectionResult>> ConnectToPeerAsync(
          PublicIdentityId targetPeerId,
          DeviceId? targetDeviceId = null,
          RelayEndpoint? relayEndpoint = null,
          CancellationToken ct = default);

      Task<DomainResult<PeerConnectionResult>> ConnectToDirectPeerAsync(
          PublicIdentityId targetPeerId,
          BlindedRoutingToken routingToken,
          DeliveryToken deliveryToken,
          Uri directEndpoint,
          DeviceId? targetDeviceId = null,
          CancellationToken ct = default);
  }
  ```

- **Algorithm:**
  1. `PeerConnectionService.ConnectToDirectPeerAsync`:
     - Register `directEndpoint` into `IStreamRegistryManagement.RegisterDirectEndpoint(targetPeerId, directEndpoint)`.
     - Construct and persist `PeerMailboxRoutingCapability(targetPeerId, routingToken, deliveryToken, RelayEndpoint.FromHostAndPort(directEndpoint.Host, directEndpoint.Port))` via `IPeerMailboxRoutingRepository.SaveAsync`.
     - Fetch consumable prekey bundle via `_preKeyBundleProvider.FetchConsumableBundleAsync(targetPeerId, targetDeviceId, RelayEndpoint.FromHostAndPort(directEndpoint.Host, directEndpoint.Port), ct)`.
     - Execute X3DH handshake via `_handshakeService.InitiateHandshakeAsync`.
     - Create local `DirectChannel` via `_channelRepository.SaveDirectAsync`.
     - Return `PeerConnectionResult(channelId, bundle.DeviceId)`.
  2. `DirectHandshakeOrchestrator`:
     - Delete manual gRPC client call to `UnauthenticatedDeliveryService.FetchPreKeyBundleAsync`.
     - Delete manual `PreKeyBundle` instantiation.
     - Delete `EnsureChannelAsync` cross-host database writing (peeking into recipient's repository).
     - Initiate connection via `connectionService.ConnectToDirectPeerAsync(...)`.
     - Dispatch handshake envelope via `initiator.FlushOutboxAsync(ct)`.
  3. The recipient node's `IInboundIngressPipeline` processes the handshake envelope off the wire, derives shared keys, and stores its own channel and contact aggregates locally.

- **Verify:**
  - `Percolator.Application.Tests/Services/PeerConnectionServiceTests.cs`
  - `Percolator.Simulator.Tests/DirectChat/DirectHandshakeOrchestratorTests.cs`
  - Command: `dotnet test Percolator.Application.Tests --filter "FullyQualifiedName~PeerConnectionService"`
  - Command: `dotnet run --project Percolator.Simulator --framework net10.0-windows10.0.19041.0 -- --replay Scenarios`

- **QMS:** [`SYS-CONTACT-CONSENT-GATE`], [`SYS-DIRECT-CONTACT-REQUEST-OUTBOX`], [`INV-UNSOLICITED-CONNECTION-SILENCE`]

---

### CONN-05 ⬛ Eliminate Relay Provisioning Duplication & Hardcoded Loopback

- **Files:**
  - `Modify`: `Percolator.Simulator/Features/RelayedChat/RelayProvisioningCoordinator.cs` (~L85, replace `"127.0.0.1"` with `relay.BoundEndpoint.Host`; ~L111, replace `"127.0.0.1"` with `relay.BoundEndpoint.Host`; ~L184, replace `"127.0.0.1"` with `relay.BoundEndpoint.Host`; ~L106-135, eliminate duplicate manual gRPC call to `NearbyDiscoveryServiceClient`)

- **Algorithm:**
  1. Replace hardcoded `"127.0.0.1"` string literals with `relay.BoundEndpoint.Host` and `relay.BoundPort`.
  2. Drive mailbox registration and linking strictly through `IRelayDirectory.EnsureMailboxProvisionedAsync` and `IRelayDirectory.GetMailboxTokensAsync`.
  3. Drive prekey publication strictly through `IRelayMailboxProvisioner.PublishPreKeyBundleAsync`.
  4. Ensure all URI and endpoint constructions reference `relay.BoundEndpoint`, allowing personas to connect to relays hosted on any LAN IP or domain.

- **Verify:**
  - `Percolator.Simulator.Tests/RelayedChat/RelayProvisioningCoordinatorTests.cs`
  - Command: `dotnet test Percolator.ApplicationIntegrationTests --filter "FullyQualifiedName~RelayedChat"`
  - Command: `dotnet run --project Percolator.Simulator --framework net10.0-windows10.0.19041.0 -- --replay Scenarios`

- **QMS:** [`SYS-DISCOVERY-PREKEY-ISOLATION`], [`SYS-SEALED-SENDER-CAPABILITY-GATING`]

---

### CONN-06 ⬛ Non-Invasive Simulator Chaos & Fault Injection via Decorators

- **Files:**
  - `Create`: `Percolator.Simulator/Features/Chaos/ISimulatorChaosController.cs`
  - `Create`: `Percolator.Simulator/Features/Chaos/SimulatorChaosController.cs`
  - `Create`: `Percolator.Simulator/Features/Chaos/ChaosRelayMailboxCapabilityStoreDecorator.cs`
  - `Create`: `Percolator.Simulator/Features/Chaos/ChaosPendingIngressStoreDecorator.cs`
  - `Modify`: `Percolator.Simulator/Features/RelayedChat/SimulatedRelayHost.cs` (~L159, wrap `_capabilityStore` with `ChaosRelayMailboxCapabilityStoreDecorator`)
  - `Modify`: `Percolator.Simulator/Features/Topology/SimulatedPersonaHost.cs` (~L260, wrap `IPendingIngressStore` with `ChaosPendingIngressStoreDecorator`)

- **Contracts:**
  ```csharp
  namespace Percolator.Simulator.Features.Chaos;

  public enum ChaosFaultAction
  {
      Pass = 0,
      DropEnvelope = 1,
      SimulateTransientError = 2,
      InjectArtificialDelay = 3
  }

  public interface ISimulatorChaosController
  {
      ChaosFaultAction EvaluateDeliveryFault(SimulatedNodeId nodeId);
      void SetFaultAction(SimulatedNodeId nodeId, ChaosFaultAction action);
      void ResetFaults(SimulatedNodeId nodeId);
  }
  ```

- **Algorithm:**
  1. `SimulatorChaosController` provides thread-safe in-memory mapping of `SimulatedNodeId` to `ChaosFaultAction`.
  2. `ChaosRelayMailboxCapabilityStoreDecorator` implements `IRelayMailboxCapabilityStore`. On `StoreEnvelopeAsync`, it checks `_chaosController.EvaluateDeliveryFault(_relayNodeId)`:
     - `DropEnvelope`: returns `DomainResult.Success()` without saving to underlying SQLite store.
     - `SimulateTransientError`: returns `DomainResult.Failure(new DomainError("CHAOS_TRANSIENT_FAULT", "Injected transient failure."))`.
     - `InjectArtificialDelay`: awaits `Task.Delay(500)` before delegating to inner store.
     - `Pass`: delegates directly to inner store.
  3. `ChaosPendingIngressStoreDecorator` decorates `IPendingIngressStore` on simulated persona hosts to simulate ingress drop or queue saturation.
  4. Assemblies `Percolator.Domain`, `Percolator.Application`, `Percolator.Infrastructure`, and `Percolator.Desktop` contain zero chaos code or testing methods.

- **Verify:**
  - `Percolator.Simulator.Tests/Chaos/SimulatorChaosDecoratorTests.cs`
  - Command: `dotnet test Percolator.Simulator.Tests --filter "FullyQualifiedName~Chaos"`
  - Command: `dotnet run --project Percolator.Simulator --framework net10.0-windows10.0.19041.0 -- --replay Scenarios`

- **QMS:** [`DEP-DESKTOP-SEGREGATION-ROLE`], [`SYS-BLOCK-WIRE-INDISTINGUISHABILITY`]

---

### CONN-07 ⬛ Desktop Add Connection Dialog (`+` Modal) & Progressive Feedback

- **Files:**
  - `Create`: `Percolator.Desktop/Features/Transports/Views/AddConnectionDialog.axaml` & `Percolator.Desktop/Features/Transports/Views/AddConnectionDialog.axaml.cs`
  - `Create`: `Percolator.Desktop/Features/Transports/ViewModels/AddConnectionDialogViewModel.cs`

- **Contracts & ViewModel:**
  ```csharp
  namespace Percolator.Desktop.Features.Transports.ViewModels;

  public sealed partial class AddConnectionDialogViewModel : ViewModelBase
  {
      [ObservableProperty] private int _selectedTabIndex;
      [ObservableProperty] private string _peerInvitationInput = string.Empty;
      [ObservableProperty] private string _relayEndpointInput = string.Empty;
      [ObservableProperty] private bool _seedPreKeysToRelay;
      [ObservableProperty] private bool _isProbing;
      [ObservableProperty] private string _probeStatusText = string.Empty;
      [ObservableProperty] private string? _errorMessage;

      [RelayCommand] private Task PasteClipboardAsync();
      [RelayCommand] private Task SubmitPeerConnectionAsync();
      [RelayCommand] private Task SubmitRelayConnectionAsync();
  }
  ```

- **Algorithm:**
  1. **UI Prefix Affordance & Auto-Stripping:**
     - Peer connection input box is preceded by a static UI prefix label: `percolator://`.
     - When the user pastes or inputs a string containing `percolator://`, the ViewModel silently strips the scheme prefix.
     - `PasteClipboardAsync` retrieves clipboard text via Avalonia `TopLevel.Clipboard`, cleanses prefixes, and sets `PeerInvitationInput`.
  2. **Tab 1: Connect to Peer (Direct P2P):**
     - Submitting invokes `IPeerInvitationCodec.Decode(PeerInvitationInput)`.
     - Begins progressive status feedback (*"Resolving endpoint..."* -> *"Probing network route..."*).
     - **Fast 2–3s Offline Timeout:**
       - Probes endpoint with a 3-second cancellation token timeout.
       - If unreachable within timeout, the dialog does **not** trap the user in an error. Instead, it completes with result `OutboundConnectionStatus.PendingOffline`, dismisses the dialog, and dispatches creation of an Outbound-Pending DM Placeholder (CONN-08).
       - If reachable, initiates handshake via `IPeerConnectionService.ConnectToDirectPeerAsync`, creates active DM, and closes modal.
  3. **Tab 2: Add Relay:**
     - Input field with static prefix `percolator://` (or `relay://`). Prompts: `host:port`.
     - Validates connectivity over TLS 1.3 without certificate pinning (relying on E2E ratchet & sealed sender).
     - Single Persona Isolation: Binds relay strictly and exclusively to active persona.
     - Prekey Seeding Opt-In: Checkbox toggle *"Publish prekey bundle to relay for offline messaging"* (unchecked by default per `SYS-PRE-ACTIVATION-STEALTH`). If checked, publishes initial prekey bundle via `IRelayMailboxProvisioner`.

- **Verify:**
  - `Percolator.Desktop.Tests/Features/Transports/AddConnectionDialogViewModelTests.cs`
  - Command: `dotnet test Percolator.Desktop.Tests --filter "FullyQualifiedName~AddConnectionDialogViewModelTests"`

- **QMS:** [`DEP-DESKTOP-UNSOLICITED-CONSENT-GATE`], [`SYS-PRE-ACTIVATION-STEALTH`], [`SYS-DISCOVERY-PREKEY-ISOLATION`]

---

### CONN-08 ⬛ Desktop Outbound-Pending Direct Connection State & Background Retry Service

- **Retry & Cancellation Policy:** The retry loop continues indefinitely with exponential backoff (5s, 15s, 30s, capped at 60s) while the persona session is active, until either the connection successfully completes or the user cancels the request.

- **Files:**
  - `Create`: `Percolator.Desktop/Features/Transports/Models/OutboundPendingConnectionModel.cs`
  - `Create`: `Percolator.Desktop/Features/Transports/Services/IOutboundPendingRetryWorker.cs`
  - `Create`: `Percolator.Desktop/Features/Transports/Services/OutboundPendingRetryWorker.cs`
  - `Modify`: `Percolator.Desktop/Features/Transports/Services/PeerConnectionStateService.cs` (track and cancel pending outbound peers)

- **Contracts:**
  ```csharp
  namespace Percolator.Desktop.Features.Transports.Models;

  public sealed record OutboundPendingConnectionModel(
      PublicIdentityId PeerId,
      string DisplayName,
      PeerInvitationPayload Invitation,
      DateTimeOffset InitiatedAtUtc);
  ```

- **Algorithm:**
  1. When an outbound connection to an offline peer is initiated, `PeerConnectionStateService` adds the peer to `ObservableList<OutboundPendingConnectionModel> PendingOutboundConnections`.
  2. The Right Sidebar DM list displays a conversation item with dimmed avatar and status pill: *"Connection pending (peer offline)..."*.
  3. When selected in the Content Area:
     - Header displays an informational banner: *"Waiting for peer to come online. Messages will be queued once connected."*
     - The message composer is disabled (`IsEnabled = false`).
     - An inline action button `[ Cancel Request ]` is rendered directly inside the banner.
  4. **User Cancellation Mechanics:**
     - A user can cancel an outbound request via two distinct paths:
       - **Context Menu:** Right-clicking the pending DM conversation item in the Right Sidebar opens a context menu with `[ Cancel Connection Request ]`.
       - **Banner Button:** Clicking the inline `[ Cancel Request ]` button on the content banner.
     - Executing cancellation invokes `PeerConnectionStateService.CancelOutboundRequest(peerId)`:
       - Signals cancellation on the `CancellationTokenSource` assigned to that peer's retry task in `OutboundPendingRetryWorker`.
       - Removes the record from `PendingOutboundConnections`.
       - If currently selected in the Content Area, navigates the content frame back to the welcome state.
  5. `OutboundPendingRetryWorker` runs as a hosted background service, periodically polling endpoints (with exponential backoff: 5s, 15s, 30s, capped at 60s). It continues indefinitely until connected or cancelled.
  6. Upon successful handshake completion, transitions item from pending list to active conversation list, activates composer, and clears the pending banner.

- **Verify:**
  - `Percolator.Desktop.Tests/Features/Transports/OutboundPendingRetryWorkerTests.cs`
  - Command: `dotnet test Percolator.Desktop.Tests --filter "FullyQualifiedName~OutboundPending"`

- **QMS:** [`DEP-DESKTOP-UNSOLICITED-CONSENT-GATE`], [`SYS-CONTACT-CONSENT-GATE`]

---

### CONN-09 ⬛ Desktop Inbound Pending Requests Panel (`[Pending Requests]`)

- **Files:**
  - `Create`: `Percolator.Desktop/Features/Transports/Views/PendingRequestsPanelView.axaml` & `Percolator.Desktop/Features/Transports/Views/PendingRequestsPanelView.axaml.cs`
  - `Create`: `Percolator.Desktop/Features/Transports/ViewModels/PendingRequestsPanelViewModel.cs`
  - `Create`: `Percolator.Desktop/Features/Transports/Models/InboundPendingRequestModel.cs`

- **Contracts:**
  ```csharp
  namespace Percolator.Desktop.Features.Transports.Models;

  public sealed record InboundPendingRequestModel(
      PublicIdentityId RequesterId,
      string DisplayName,
      string FingerprintPreview,
      DateTimeOffset ReceivedAtUtc);
  ```

- **Algorithm:**
  1. The `[Pending Requests]` button is anchored at the bottom of the Left Sidebar rail, positioned strictly above the user's avatar to ensure pending request spam does not distract the user.
  2. Displays a subtle notification indicator/count badge when unsolicited inbound handshakes are pending.
  3. **Inbound Connection Silence (`INV-UNSOLICITED-CONNECTION-SILENCE`, `SYS-CONTACT-CONSENT-GATE`):** Zero OS toasts, audio pings, or unsolicited modal popups. Inbound requests are strictly isolated from the Direct Messages list.
  4. `PendingRequestsPanelViewModel` queries inbound requests using the CQRS read port `IPeerContactQueryService.GetContactSummariesAsync(activePersonaId)`, filtering contacts where `c.State == ContactState.PendingApproval`.
  5. Clicking opens the `PendingRequestsPanelView`:
     - Displays requester display name and cryptographic fingerprint preview (`4a8f...b12e`).
     - Actions:
       - **Accept**: Invokes `IHandshakeService.CompletePendingHandshakeAsync(recipientId, senderId, ct)`, derives master session key and ratchet session, creates active 1-to-1 DM in the Right Sidebar, and clears the request from the pending list.
       - **Reject**: Silently deletes pending handshake via `IPendingHandshakeRepository.DeletePendingHandshakeAsync` and updates contact state to dismissed without notifying sender.
       - **Block** (formerly "Burn"): Permanently blacklists the peer ID and route per [`SYS-BLOCK-WIRE-INDISTINGUISHABILITY`] (future packets drop silently with zero TCP/wire feedback).

- **Verify:**
  - `Percolator.Desktop.Tests/Features/Transports/PendingRequestsPanelViewModelTests.cs`
  - Command: `dotnet test Percolator.Desktop.Tests --filter "FullyQualifiedName~PendingRequestsPanel"`

- **QMS:** [`DEP-DESKTOP-UNSOLICITED-CONSENT-GATE`], [`SYS-CONTACT-CONSENT-GATE`], [`SYS-BLOCK-WIRE-INDISTINGUISHABILITY`], [`INV-UNSOLICITED-CONNECTION-SILENCE`]

---

### CONN-10 ⬛ Desktop Share My Invitation Dialog & Port Range Privacy Selector

- **Files:**
  - `Create`: `Percolator.Desktop/Features/Transports/Views/ShareInvitationDialog.axaml` & `Percolator.Desktop/Features/Transports/Views/ShareInvitationDialog.axaml.cs`
  - `Create`: `Percolator.Desktop/Features/Transports/ViewModels/ShareInvitationDialogViewModel.cs`

- **Contracts:**
  ```csharp
  namespace Percolator.Desktop.Features.Transports.ViewModels;

  public enum PortRangePrivacyPreset
  {
      DefaultRange = 0,    // Omit port from invite (probe 5100-5299)
      CustomRange = 1,     // Embed discrete range (e.g. 5100-5120)
      SinglePort = 2       // Pin to currently bound listening port
  }

  public sealed partial class ShareInvitationDialogViewModel : ViewModelBase
  {
      [ObservableProperty] private PortRangePrivacyPreset _selectedPrivacyPreset;
      [ObservableProperty] private int _customRangeMin = 5100;
      [ObservableProperty] private int _customRangeMax = 5120;
      [ObservableProperty] private string _invitationUrl = string.Empty;
      [ObservableProperty] private byte[]? _qrCodePngBytes;

      [RelayCommand] private Task CopyInvitationUrlAsync();
      [RelayCommand] private void UpdateInvitationPayload();
  }
  ```

- **Algorithm:**
  1. Accessible via the Identity & Profile Drawer ("View Profile & Share Invite").
  2. Resolves current persona's `PublicIdentityId`, `MailboxRoutingToken`, `MailboxDeliveryToken`, and bound LAN IP addresses.
  3. `UpdateInvitationPayload`:
     - Based on `SelectedPrivacyPreset`:
       - `DefaultRange`: sets `PortRange = null` (receiver probes `5100-5299`).
       - `CustomRange`: sets `PortRange = new PortRange(CustomRangeMin, CustomRangeMax)`.
       - `SinglePort`: sets `PortRange = PortRange.Single(BoundPort)`.
     - Calls `IPeerInvitationCodec.Encode(payload)` to produce `percolator://invite?...` URL.
     - Generates QR code PNG bytes for visual scanning.
  4. `CopyInvitationUrlAsync`: copies URL to clipboard with confirmation toast.

- **Verify:**
  - `Percolator.Desktop.Tests/Features/Transports/ShareInvitationDialogViewModelTests.cs`
  - Command: `dotnet test Percolator.Desktop.Tests --filter "FullyQualifiedName~ShareInvitationDialog"`

- **QMS:** [`DEP-DESKTOP-OUT-OF-BAND-VERIFICATION`], [`SYS-OUT-OF-BAND-SECURITY-CODE`]

---

### CONN-11 ⬜ Bi-Directional Multi-Node Integration Suite & Scenario Automation

- **Files:**
  - `Create`: `Percolator.ApplicationIntegrationTests/Interoperability/DesktopSimulatorInteroperabilityTests.cs`
  - `Modify`: `Percolator.Simulator/Features/Scenarios/ScenarioRunnerService.cs` (add automated interoperability scenario validation)

- **Algorithm:**
  1. Multi-node integration test in `Percolator.ApplicationIntegrationTests` initializes:
     - Node A (Simulated Relay listening on `IPAddress.Any` with ephemeral TLS).
     - Node B (Simulated Persona Host listening on `IPAddress.Any`).
     - Node C (Desktop Session Host running authenticated Stage 2 container).
  2. Step 1: Node C connects to Node A (Relay) over routable LAN IP (`127.0.0.1` or LAN address), registers mailbox, seeds prekeys, and connects stream.
  3. Step 2: Node C parses invitation from Node B (Direct Peer) via `IPeerInvitationCodec`, executes `ConnectToDirectPeerAsync`, and dispatches initial handshake envelope. Node B receives envelope over wire, establishes ratchet, and verifies contact creation.
  4. Step 3: Node B sends direct connection request to Node C. Asserts Node C records pending invitation without triggering OS toasts (`INV-UNSOLICITED-CONNECTION-SILENCE`).
  5. Step 4: Configure `ISimulatorChaosController` on Node A to `DropEnvelope`. Node C sends message via relay; asserts outbox retry pipeline backs off and succeeds once fault is cleared.

- **Verify:**
  - `Percolator.ApplicationIntegrationTests/Interoperability/DesktopSimulatorInteroperabilityTests.cs`
  - Command: `dotnet test Percolator.ApplicationIntegrationTests --filter "FullyQualifiedName~DesktopSimulatorInteroperability"`
  - Command: `dotnet run --project Percolator.Simulator --framework net10.0-windows10.0.19041.0 -- --replay Scenarios`

- **QMS:** [`SYS-CONTACT-CONSENT-GATE`], [`SYS-PRE-ACTIVATION-STEALTH`], [`SYS-BLOCK-WIRE-INDISTINGUISHABILITY`], [`DEP-DESKTOP-UNSOLICITED-CONSENT-GATE`]

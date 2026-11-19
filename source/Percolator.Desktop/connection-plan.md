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
| Domain | `RelayLinkStatus` | `Percolator.Domain/Delivery/ValueObjects/RelayLinkStatus.cs` | Transient (Enum) |
| Application | `PeerInvitationPayload` | `Percolator.Application/Invitations/PeerInvitationPayload.cs` | Transient (Record) |
| Application | `IPeerInvitationCodec` | `Percolator.Application/Invitations/IPeerInvitationCodec.cs` | Singleton |
| Application | `PeerInvitationCodec` | `Percolator.Application/Invitations/PeerInvitationCodec.cs` | Singleton |
| Application | `IPeerConnectionService` | `Percolator.PluginSdk/IPeerConnectionService.cs` | Scoped |
| Application | `PeerConnectionService` | `Percolator.Application/Services/PeerConnectionService.cs` | Scoped |
| Application | `OutboundConnectionStatus` | `Percolator.Application/Connections/OutboundConnectionStatus.cs` | Transient (Enum) |
| Application | `OutboundConnectionResult` | `Percolator.Application/Connections/OutboundConnectionResult.cs` | Transient (Record) |
| Application | `IOutboundConnectionWorkflow` | `Percolator.Application/Connections/IOutboundConnectionWorkflow.cs` | Scoped |
| Application | `OutboundConnectionWorkflow` | `Percolator.Application/Connections/OutboundConnectionWorkflow.cs` | Scoped |
| Application | `PersonaRelayLinkReadModel` | `Percolator.Application/Delivery/Ports/IPersonaRelayQueryService.cs` | Transient (Record) |
| Application | `IPersonaRelayQueryService` | `Percolator.Application/Delivery/Ports/IPersonaRelayQueryService.cs` | Scoped |
| Infrastructure | `SqlPersonaRelayQueryService` | `Percolator.Infrastructure/Persistence/Queries/SqlPersonaRelayQueryService.cs` | Scoped |
| Infrastructure | `PeerTransportServerOptions` | `Percolator.Infrastructure/Transport/PeerTransportServerOptions.cs` | Singleton |
| Infrastructure | `TransportCertificateSanBuilder` | `Percolator.Infrastructure/Transport/TransportCertificateSanBuilder.cs` | Static (Internal Helper) |
| Simulator | `ISimulatorChaosController` | `Percolator.Simulator/Features/Chaos/ISimulatorChaosController.cs` | Singleton |
| Simulator | `SimulatorChaosController` | `Percolator.Simulator/Features/Chaos/SimulatorChaosController.cs` | Singleton |
| Simulator | `SimulatorChaosTransportDispatcherDecorator` | `Percolator.Simulator/Features/Chaos/SimulatorChaosTransportDispatcherDecorator.cs` | Scoped (Egress Pipeline) |
| Simulator | `SimulatorOutboxFlushingIngressPipelineDecorator` | `Percolator.Simulator/Features/Topology/SimulatorOutboxFlushingIngressPipelineDecorator.cs` | Scoped (Ingress Pipeline) |
| Simulator | `ChaosRelayMailboxCapabilityStoreDecorator` | `Percolator.Simulator/Features/Chaos/ChaosRelayMailboxCapabilityStoreDecorator.cs` | Scoped / Singleton |
| Simulator | `SimulatorAdvertisedHostKind` | `Percolator.Simulator/Features/Topology/SimulatorAdvertisedHostKind.cs` | Transient (Enum) |
| Simulator | `PersonaConnectPeerFlyoutViewModel` | `Percolator.Simulator/Features/Topology/PersonaConnectPeerFlyoutViewModel.cs` | Transient |
| Simulator | `PersonaInvitationExportFlyoutViewModel` | `Percolator.Simulator/Features/Topology/PersonaInvitationExportFlyoutViewModel.cs` | Transient |
| Simulator | `RelayRegistrationFlyoutViewModel` | `Percolator.Simulator/Features/Topology/RelayRegistrationFlyoutViewModel.cs` | Transient |
| Desktop | `MainWindowViewModel` | `Percolator.Desktop/Features/Shell/ViewModels/MainWindowViewModel.cs` | Transient |
| Desktop | `PendingRequestsPanelWindow` | `Percolator.Desktop/Features/Transports/Views/PendingRequestsPanelWindow.axaml` | Transient (Window) |
| Desktop | `PendingRequestsPanelViewModel` | `Percolator.Desktop/Features/Transports/ViewModels/PendingRequestsPanelViewModel.cs` | Transient |
| Desktop | `AddConnectionDialogViewModel` | `Percolator.Desktop/Features/Transports/ViewModels/AddConnectionDialogViewModel.cs` | Transient |
| Desktop | `OutboundPendingConnectionRetryWorker` | `Percolator.Desktop/Features/Transports/Services/OutboundPendingConnectionRetryWorker.cs` | Singleton (Hosted Service) |
| Desktop | `ShareInvitationDialogViewModel` | `Percolator.Desktop/Features/Transports/ViewModels/ShareInvitationDialogViewModel.cs` | Transient |
| Desktop | `PersonaRelayItemViewModel` | `Percolator.Desktop/Features/Startup/ViewModels/CreatePersonaDialogViewModel.cs` | Transient |

---

## Tickets

### CONN-01 ✅ Multi-Interface Network Listener Binding (`0.0.0.0` IPv4)
Updated Kestrel transport servers, exchange directories, and simulator persona/relay hosts to bind strictly to IPv4 `IPAddress.Any` (`0.0.0.0`). Sockets activate only upon explicit node startup, ensuring pre-activation stealth while allowing cross-process and LAN connectivity.

---

### CONN-02 ✅ Dynamic Routable LAN Endpoint Resolution & Certificate SAN Expansion
Introduced `TransportCertificateSanBuilder` to dynamically populate all active host network interface IP addresses and DNS hostnames into ephemeral TLS 1.3 certificate Subject Alternative Names (SANs). Personas and relays resolve primary LAN endpoints dynamically, eliminating hardcoded loopback addresses.

---

### CONN-03 ✅ Out-of-Band Invitation Codec & Port Range Settings
Implemented `PortRange` domain value object and `PeerInvitationCodec` for encoding and decoding `percolator://invite?...` URLs and base64 payloads containing peer IDs, routing/delivery tokens, host candidates, and port probing ranges. Extended SQLite vault settings to persist preferred port ranges.

---

### CONN-04 ✅ Eliminate Direct Handshake Duplication & Anti-Shortcut Cross-Host Peeking
Added `IPeerConnectionService.ConnectToDirectPeerAsync` to drive direct P2P handshakes through official application ports. Eliminated duplicate manual gRPC prekey bundle calls and cross-host database writing in `DirectHandshakeOrchestrator`, routing all ingress cleanly through wire pipelines.

---

### CONN-05 ✅ Eliminate Relay Provisioning Duplication & Hardcoded Loopback
Refactored `RelayProvisioningCoordinator` to use dynamic relay endpoints instead of hardcoded `127.0.0.1` strings. Provisioning, token retrieval, and prekey publishing now route strictly through standard domain interfaces (`IRelayDirectory`, `IRelayMailboxProvisioner`).

---

### CONN-06 ✅ Non-Invasive Simulator Chaos & Fault Injection via Decorators
Introduced `ISimulatorChaosController` and non-invasive decorator implementations (`ChaosRelayMailboxCapabilityStoreDecorator`, `ChaosPendingIngressStoreDecorator`) in the Simulator layer. Enables simulating envelope drops, latency, and transient failures during testing without polluting production domain or infrastructure assemblies.

---

### CONN-07 ✅ Desktop Add Connection Dialog (`+` Modal) & Progressive Feedback
Created `AddConnectionDialog` and `AddConnectionDialogViewModel` with Tab 1 for P2P invitations (featuring UI prefix stripping, clipboard pasting, and 3-second offline probe timeout fallback) and Tab 2 for relay registration (with single-persona isolation and opt-in prekey seeding).

---

### CONN-08 ✅ Desktop Outbound-Pending Direct Connection State & Background Retry Service
Implemented `OutboundPendingRetryWorker` and `OutboundPendingConnectionModel` for managing initiated outbound connections to offline peers. Displays dimmed placeholder conversations in the sidebar with banner cancellation controls and runs an exponential backoff background retry loop.

---

### CONN-09 ✅ Desktop Inbound Pending Requests Panel (`[Pending Requests]`)
Created `PendingRequestsPanelView` and `PendingRequestsPanelViewModel` to display unsolicited inbound connection offers isolated from the DM list. Enforces `INV-UNSOLICITED-CONNECTION-SILENCE` with zero notification noise, offering discrete Accept, Reject, and Block actions.

---

### CONN-10 ✅ Desktop Share My Invitation Dialog & Port Range Privacy Selector
Created `ShareInvitationDialog` and `ShareInvitationDialogViewModel` to generate and export out-of-band invitation URLs and QR codes. Provides a privacy selector to choose between default dynamic range (`5100-5299`), custom port range, or pinned single listening port.

---

### CONN-11 ✅ Desktop Shell Realignment & Navigation Rail Dialog Orchestration (`shell-flow.md`)
Realigned the Desktop Left Sidebar navigation rail grid per `shell-flow.md` layout invariants, wiring `AddConnectionDialog`, `PendingRequestsPanelWindow`, and `ShareInvitationDialog` through `IWindowManager.ShowDialogAsync`. Wired dynamic pending request count badges while enforcing inbound privacy silence (`INV-UNSOLICITED-CONNECTION-SILENCE`).

---

### CONN-12 ✅ Simulator Dual-Mode Peer & Relay Interoperability (In-World & External with IP Override)
Implemented dual-mode candidate host resolution (Auto LAN IP, Loopback, and Custom Host) and port privacy presets for simulated persona invitation URLs and relay endpoints. Added flyouts on persona and relay cards to export `percolator://invite?...` URLs and connect to peers or register relays across real network interfaces.

---

### CONN-12b ✅ Simulator Architecture Realignment: Ingress/Egress Wire Pipelines, Application Outbound Workflow & High-Density Card UI
Unified Desktop and Simulator connection mechanics using `IOutboundConnectionWorkflow` in `Percolator.Application`. Replaced simulator SQLite store tampering with clean `SimulatorChaosTransportDispatcherDecorator` decorating `ITransportDispatcher` on egress. Eliminated coordinator database backdoors in `DirectHandshakeOrchestrator` and `DirectChatCoordinator` by ensuring `PeerContact` creation on outbound connection establishment in `PeerConnectionService`. Consolidated `PersonaCardView` toolbar into 3 non-blocking flyouts (`🔗 Connect Peer ▼`, `📋 Share Invite ▼`, `📬 Relay ▼`) and tucked 14 options into `⚙️ Persona Settings ▼`.

---

### CONN-12c ✅ Scheme Unification (`percolator://`), Canonical Ports (5100–5199), Honest Relay UI & Simulator Decoupling
Enforced `percolator://` as canonical URI scheme across all network/UI surfaces with transparent mapping to `https://` in `PeerGrpcChannelFactory`. Unified canonical port range `5100–5199` across domain and transport infrastructure. Simplified simulator relay export to a direct 1-click clipboard button, and decoupled coordinators from direct recipient DB/RAM writes by routing handshakes strictly through wire ingress and outbox pipelines. Eradicated unused DB port leasing artifacts (`IOpsPortReservationService`) and segregated simulation reset lifecycle (`ISimulatedPortReservationService`).

---

### CONN-12d ✅ Persona-Scoped Relay Management, CQRS Query Service & Honest Identity Gate Exchanges

- **Specification Governance:**
  1. **Pre-Activation Seam & CQRS Query Port Discipline:**
     Per [`DEP-DESKTOP-PRE-ACTIVATION-STEALTH`](../spec/deployable-requirements.md) and [`INV-DESKTOP-STEALTH-ACTIVATION-GATE`](Features/Startup/ViewModels/IdentityGateViewModel.cs), the Identity Gate runs post-unlock using the Stage 2 authenticated session container (`session.Services`), but before the main shell and before any network listeners or background workers activate (`session.StartAsync`). Per [`implementation-plan-spec`](../spec/implementation-plan.spec.md#4-strict-ddd-hexagonal-rules) §4.8 and QMS Principle 6 (CQRS Read-Path Guidance), UI read projections MUST NOT load write-heavy domain aggregates (`RelayRegistration`) into memory. A dedicated query service `IPersonaRelayQueryService` reads lightweight projection models (`PersonaRelayLinkReadModel`) directly from EF Core with zero domain entity tracking, zero cryptographic key loading, and zero network side effects.
  2. **Honest Identity Gate Exchanges:**
     The Identity Gate exists exclusively to acquire local port leases (`IPortLease`) and launch local listening servers. Remote relays do not listen on local sockets; presenting them as launchable tiles is misleading and results in a no-op placebo toggle. The Identity Gate's `_exchangeList` strictly filters for `IsSelfHosted == true`, eliminating remote relays from the pre-activation screen.
  3. **Persona-Scoped Relay Association & Persisted Enable/Disable Toggle:**
     Relays are mailbox hosts linked to personas (`RelayMailboxBinding`). In the persona customization/edit surface (`CreatePersonaDialogViewModel` in edit mode), the user can view all relays linked to that persona and selectively toggle `RelayLinkStatus` (`Enabled` / `Disabled`). The selection persists to `RelayMailboxBinding.Status` in SQLite via `IRelayDirectory.SetMailboxBindingStatusAsync`.
  4. **Egress & Ingress Traffic Silence for Disabled Relays:**
     `DrainScheduler`, `PreKeyMaintenanceService`, and `ProvisionLinkedMailboxesAsync` filter targets to `RelayLinkStatus.Enabled`, guaranteeing disabled relays generate zero network requests, zero polling, and zero prekey uploads.

- **Files:**
  - `Create`: `Percolator.Domain/Delivery/ValueObjects/RelayLinkStatus.cs`
  - `Modify`: `Percolator.Domain/Delivery/Model/RelayMailboxBinding.cs`
  - `Modify`: `Percolator.Domain/Delivery/Model/RelayRegistration.cs`
  - `Create`: `Percolator.Application/Delivery/Ports/IPersonaRelayQueryService.cs`
  - `Modify`: `Percolator.Application/Delivery/Ports/IRelayDirectory.cs`
  - `Modify`: `Percolator.Application/Delivery/RelayDirectoryService.cs`
  - `Modify`: `Percolator.Application/Delivery/DrainScheduler.cs`
  - `Modify`: `Percolator.Infrastructure/Persistence/Entities/RelayEntities.cs`
  - `Modify`: `Percolator.Infrastructure/Persistence/Repositories/SqliteRelayRegistrationRepository.cs`
  - `Create`: `Percolator.Infrastructure/Persistence/Queries/SqlPersonaRelayQueryService.cs`
  - `Modify`: `Percolator.Infrastructure/DependencyInjection/InfrastructureServiceCollectionExtensions.cs`
  - `Modify`: `Percolator.Desktop/Common/Session/DesktopSessionServiceCollectionExtensions.cs`
  - `Modify`: `Percolator.Desktop/Features/Startup/ViewModels/IdentityGateViewModel.cs`
  - `Modify`: `Percolator.Desktop/Features/Startup/ViewModels/CreatePersonaDialogViewModel.cs`
  - `Modify`: `Percolator.Desktop/Features/Startup/Views/CreatePersonaDialogWindow.axaml`
  - `Create`: `Percolator.Domain.Tests/Delivery/RelayRegistrationBindingToggleTests.cs`
  - `Create`: `Percolator.Infrastructure.Tests/Persistence/Queries/SqlPersonaRelayQueryServiceTests.cs`
  - `Modify`: `Percolator.Desktop.Tests/Startup/IdentityGateViewModelTests.cs`
  - `Modify`: `Percolator.Desktop.Tests/Startup/CreatePersonaDialogViewModelTests.cs`

- **Technical Implementation Steps:**
  1. **Domain Model & Value Object:**
     - In `Percolator.Domain/Delivery/ValueObjects/RelayLinkStatus.cs`:
       ```csharp
       namespace Percolator.Domain.Delivery.ValueObjects;

       public enum RelayLinkStatus
       {
           Enabled = 1,
           Disabled = 2
       }
       ```
     - In `RelayMailboxBinding.cs`:
       ```csharp
       public sealed record RelayMailboxBinding(
           PublicIdentityId OwnerIdentityId,
           BlindedRoutingToken RoutingToken,
           DeliveryToken DrainToken,
           DateTimeOffset LinkedAtUtc,
           DeliveryToken? PreviousDrainToken = null,
           DateTimeOffset? MailboxProvisionedUtc = null,
           RelayLinkStatus Status = RelayLinkStatus.Enabled);
       ```
     - In `RelayRegistration.cs`:
       ```csharp
       public DomainResult SetBindingStatus(PublicIdentityId ownerIdentityId, RelayLinkStatus status)
       {
           var index = _bindings.FindIndex(b => b.OwnerIdentityId == ownerIdentityId);
           if (index < 0)
           {
               return DomainResult.Failure(new DomainError("MAILBOX_BINDING_NOT_FOUND", "Identity is not linked to this relay."));
           }
           _bindings[index] = _bindings[index] with { Status = status };
           return DomainResult.Success();
       }
       ```

  2. **Application CQRS Query Port & Relay Directory Port:**
     - In `Percolator.Application/Delivery/Ports/IPersonaRelayQueryService.cs`:
       ```csharp
       namespace Percolator.Application.Delivery.Ports;

       using System.Collections.Generic;
       using System.Threading;
       using System.Threading.Tasks;
       using Percolator.Domain.Delivery.ValueObjects;
       using Percolator.Domain.Identities;

       public sealed record PersonaRelayLinkReadModel(
           PublicIdentityId RelayIdentityId,
           string DisplayName,
           RelayEndpoint Endpoint,
           bool IsLocalRelayHost,
           RelayLinkStatus Status);

       public interface IPersonaRelayQueryService
       {
           Task<IReadOnlyList<PersonaRelayLinkReadModel>> GetRelayLinksForPersonaAsync(
               PublicIdentityId personaId,
               CancellationToken ct = default);
       }
       ```
     - In `IRelayDirectory.cs`:
       Add:
       ```csharp
       Task<DomainResult> SetMailboxBindingStatusAsync(
           PublicIdentityId relayIdentityId,
           PublicIdentityId ownerIdentityId,
           RelayLinkStatus status,
           CancellationToken ct = default);
       ```
     - In `RelayDirectoryService.cs`:
       Implement `SetMailboxBindingStatusAsync`:
       ```csharp
       public async Task<DomainResult> SetMailboxBindingStatusAsync(
           PublicIdentityId relayIdentityId,
           PublicIdentityId ownerIdentityId,
           RelayLinkStatus status,
           CancellationToken ct = default)
       {
           var registration = await _relayRepository.GetByIdAsync(relayIdentityId, ct).ConfigureAwait(false);
           if (registration is null)
           {
               return DomainResult.Failure(new DomainError("RELAY_NOT_FOUND", "Relay registration not found."));
           }
           var result = registration.SetBindingStatus(ownerIdentityId, status);
           if (result.IsFailure) return result;
           await _relayRepository.SaveAsync(registration, ct).ConfigureAwait(false);
           return DomainResult.Success();
       }
       ```
       In `RelayDirectoryEntry`: add `IReadOnlyList<PublicIdentityId> EnabledOwnerIdentityIds` populated from bindings where `Status == RelayLinkStatus.Enabled`.
       In `ProvisionLinkedMailboxesAsync`: iterate only `registration.Bindings.Where(b => b.Status == RelayLinkStatus.Enabled)`.
     - In `DrainScheduler.cs` (~L165):
       Filter target generation:
       ```csharp
       var desired = entries
           .SelectMany(e => e.EnabledOwnerIdentityIds
               .Where(active.Contains)
               .Select(o => new MailboxDrainTarget(o, e.RelayIdentityId, e.Endpoint)))
           .ToHashSet();
       ```

  3. **Infrastructure Persistence & EF Core Mapping:**
     - In `RelayEntities.cs` (`RelayMailboxBindingEntity`):
       ```csharp
       public int Status { get; set; } = 1; // 1 = Enabled, 2 = Disabled
       ```
     - In `SqliteRelayRegistrationRepository.cs`:
       Map `Status = (int)binding.Status` on save, and `(RelayLinkStatus)entity.Status` on rehydrate.
     - In `Percolator.Infrastructure/Persistence/Queries/SqlPersonaRelayQueryService.cs`:
       ```csharp
       namespace Percolator.Infrastructure.Persistence.Queries;

       using System;
       using System.Collections.Generic;
       using System.Linq;
       using System.Threading;
       using System.Threading.Tasks;
       using Microsoft.EntityFrameworkCore;
       using Percolator.Application.Delivery.Ports;
       using Percolator.Domain.Delivery.ValueObjects;
       using Percolator.Domain.Identities;
       using Percolator.Infrastructure.Persistence;

       public sealed class SqlPersonaRelayQueryService : IPersonaRelayQueryService
       {
           private readonly PercolatorDbContext _db;

           public SqlPersonaRelayQueryService(PercolatorDbContext db)
           {
               _db = db ?? throw new ArgumentNullException(nameof(db));
           }

           public async Task<IReadOnlyList<PersonaRelayLinkReadModel>> GetRelayLinksForPersonaAsync(
               PublicIdentityId personaId,
               CancellationToken ct = default)
           {
               var ownerGuid = personaId.Value;
               var query = from b in _db.RelayMailboxBindings.AsNoTracking()
                           join r in _db.RelayRegistrations.AsNoTracking() on b.RelayIdentityId equals r.RelayIdentityId
                           join p in _db.Profiles.AsNoTracking() on r.RelayIdentityId equals p.IdentityId into profiles
                           from prof in profiles.DefaultIfEmpty()
                           where b.OwnerIdentityId == ownerGuid
                           select new PersonaRelayLinkReadModel(
                               new PublicIdentityId(r.RelayIdentityId),
                               prof != null ? prof.DisplayName : r.Endpoint,
                               RelayEndpoint.Create(r.Endpoint).Value!,
                               prof != null && prof.Role == 2,
                               (RelayLinkStatus)b.Status);

               return await query.ToListAsync(ct).ConfigureAwait(false);
           }
       }
       ```
     - In `InfrastructureServiceCollectionExtensions.cs` and `DesktopSessionServiceCollectionExtensions.cs`:
       Register `services.AddScoped<IPersonaRelayQueryService, SqlPersonaRelayQueryService>();`.

  4. **Desktop UI Realignment (Honest Gate & Persona Editor):**
     - In `IdentityGateViewModel.cs` (`LoadAsync` ~L188):
       Filter exchange entries to self-hosted only:
       ```csharp
       foreach (var exchangeEntry in exchangeEntries.Where(e => e.IsSelfHosted))
       {
           var tile = BuildExchangeTile(exchangeEntry);
           _exchangeList.Add(tile);
           await ProbeExchangeAsync(tile, ct).ConfigureAwait(true);
       }
       ```
     - In `CreatePersonaDialogViewModel.cs`:
       - Inject `IPersonaRelayQueryService` and `IRelayDirectory`.
       - Add presentation model:
         ```csharp
         public sealed partial class PersonaRelayItemViewModel : ObservableObject
         {
             public PublicIdentityId RelayId { get; }
             public string DisplayName { get; }
             public string Endpoint { get; }
             public bool IsLocalHost { get; }

             [ObservableProperty]
             private bool _isEnabled;

             public PersonaRelayItemViewModel(PersonaRelayLinkReadModel model)
             {
                 RelayId = model.RelayIdentityId;
                 DisplayName = model.DisplayName;
                 Endpoint = model.Endpoint.Value;
                 IsLocalHost = model.IsLocalRelayHost;
                 _isEnabled = model.Status == RelayLinkStatus.Enabled;
             }
         }
         ```
       - In `InitializeForEdit`: populate `ObservableCollection<PersonaRelayItemViewModel> ConnectedRelays` via `_queryService.GetRelayLinksForPersonaAsync(identityId)`.
       - In `Save()`: for any modified item, invoke `_relayDirectory.SetMailboxBindingStatusAsync(item.RelayId, _candidateId, item.IsEnabled ? RelayLinkStatus.Enabled : RelayLinkStatus.Disabled)`.
     - In `CreatePersonaDialogWindow.axaml`:
       Under edit mode, render `ConnectedRelays` with a header `"Connected Relays"`, display name, endpoint text, and a toggle control bound to `IsEnabled`.

- **Semantic Unit Tests:**
  - `RelayRegistrationBindingToggleTests`:
    - `SetBindingStatus_UpdatesStatus_WhenBindingExists`
    - `SetBindingStatus_ReturnsNotFoundError_WhenBindingDoesNotExist`
    - `DefaultBindingStatus_IsEnabled`
  - `SqlPersonaRelayQueryServiceTests`:
    - `GetRelayLinksForPersonaAsync_ReturnsOnlyBindingsForTargetPersona`
    - `GetRelayLinksForPersonaAsync_ProjectsCorrectRelayLinkStatus`
    - `GetRelayLinksForPersonaAsync_ReturnsEmptyWhenNoBindingsExist`
  - `IdentityGateViewModelTests`:
    - `LoadAsync_OnlyAddsSelfHostedExchangesToExchangesList`
  - `CreatePersonaDialogViewModelTests`:
    - `InitializeForEdit_LoadsConnectedRelaysFromQueryService`
    - `Save_PersistsUpdatedRelayLinkStatus`

- **Verify:**
  - `dotnet test Percolator.Domain.Tests`
  - `dotnet test Percolator.Application.Tests`
  - `dotnet test Percolator.Infrastructure.Tests`
  - `dotnet test Percolator.Desktop.Tests`
  - `dotnet run --project Percolator.Simulator --framework net10.0-windows10.0.19041.0 -- --replay Scenarios`
  - `pwsh -File .agents/skills/architectural-plan-refinement/scripts/validate-plan.ps1 -Path Percolator.Desktop/connection-plan.md`

- **QMS:** [`SYS-PRE-ACTIVATION-STEALTH`], [`DEP-DESKTOP-PRE-ACTIVATION-STEALTH`], [`INV-DESKTOP-STEALTH-ACTIVATION-GATE`], [`INV-DESKTOP-PRESENTATION-MODELS`], [`HAZ-TRANSPORT-SPOOFING`]

---


### CONN-13 ⬜ Bi-Directional Multi-Node Integration Suite & Scenario Automation

- **Specification Governance:** Automated multi-node integration test suite verifying end-to-end interoperability between Desktop Session Host and Simulator nodes across real network interfaces and TLS 1.3 encryption.

- **Files:**
  - `Create`: `Percolator.ApplicationIntegrationTests/Interoperability/DesktopSimulatorInteroperabilityTests.cs`
  - `Modify`: `Percolator.ApplicationIntegrationTests/Percolator.ApplicationIntegrationTests.csproj` (add ProjectReferences to `Percolator.Desktop` and `Percolator.Simulator`)
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
  6. Zero arbitrary sleep delays (`Task.Delay`): Awaits explicit reactive state bus signals or outbox drain completions per Anti-Shortcut Rule 15.

- **Verify:**
  - `Percolator.ApplicationIntegrationTests/Interoperability/DesktopSimulatorInteroperabilityTests.cs`:
    - `Interoperability_DesktopRegistersOnSimulatedRelay_MailboxTokensExchangedOverWire`
    - `Interoperability_DesktopConnectsToSimulatedPersona_DirectHandshakeCompletesOverWire`
    - `Interoperability_SimulatedPersonaConnectsToDesktop_InboundSilentConsentGateVerified`
    - `Interoperability_EndToEndEncryptedChat_DoubleRatchetRatchetStepsVerified`
    - `Interoperability_RelayChaosFaultInjection_OutboxRetriesUntilCleared`
  - Command: `dotnet test Percolator.ApplicationIntegrationTests --filter "FullyQualifiedName~DesktopSimulatorInteroperability"`
  - Command: `dotnet run --project Percolator.Simulator --framework net10.0-windows10.0.19041.0 -- --replay Scenarios`

- **QMS:** [`SYS-CONTACT-CONSENT-GATE`], [`SYS-PRE-ACTIVATION-STEALTH`], [`SYS-BLOCK-WIRE-INDISTINGUISHABILITY`], [`DEP-DESKTOP-UNSOLICITED-CONSENT-GATE`], [`HAZ-PREKEY-STARVATION`], [`SYS-MUTUAL-AUTHENTICATED-WIRE-TRANSPORT`]

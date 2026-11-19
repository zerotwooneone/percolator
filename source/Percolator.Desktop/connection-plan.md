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
| Application | `OutboundConnectionStatus` | `Percolator.Application/Connections/OutboundConnectionStatus.cs` | Transient (Enum) |
| Application | `OutboundConnectionResult` | `Percolator.Application/Connections/OutboundConnectionResult.cs` | Transient (Record) |
| Application | `IOutboundConnectionWorkflow` | `Percolator.Application/Connections/IOutboundConnectionWorkflow.cs` | Scoped |
| Application | `OutboundConnectionWorkflow` | `Percolator.Application/Connections/OutboundConnectionWorkflow.cs` | Scoped |
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

- **Specification Governance:**
  1. **Canonical Scheme Invariant (`percolator://`)**: Enforces `percolator://` as the sole canonical URI scheme for all network and UI surfaces per [`DEC-CANONICAL-URI-SCHEME`](../DecisionRegister.md#3-integration-transport-and-platform-decisions). Rejects obsolete schemes (`relay://`, `https://`) in user and dialog inputs. Inward port and domain value object `RelayEndpoint` formats strings as `percolator://{host}:{port}`. Outward adapter `PeerGrpcChannelFactory` transparently translates `percolator://` URIs to `https://` exclusively when calling `SocketsHttpHandler` / `GrpcChannel.ForAddress`.
  2. **Canonical Port Range Invariant (5100–5199)**: Sets canonical port boundary to `5100–5199` inclusive (100 discrete ports) across domain value object `PortRange.DefaultDynamicRange` and `PortReservationService` per [`DEC-CANONICAL-PORT-RANGE`](../DecisionRegister.md#3-integration-transport-and-platform-decisions).
  3. **Relay Card Ergonomics & Honest UI**: Replaces the multi-field `RelayEndpointExportFlyoutViewModel` flyout on `RelayCardView.axaml` with a direct 1-click `📋 Copy Endpoint` button executing `CopyEndpointCommand`, copying `percolator://127.0.0.1:{BoundPort}` to the clipboard. Binds `SimulatedRelayHost` explicitly to `IPAddress.Loopback` (`127.0.0.1`).
  4. **Coordinator Database & RAM Decoupling**: Eliminates cross-host recipient database pre-population and manual contact/channel insertions from `RelayProvisioningCoordinator.InitiateRelayedHandshakeAsync`. Delegates handshake initiation strictly to the initiator host's `IPeerConnectionService.ConnectToPeerAsync`, allowing the recipient to establish channels and contacts naturally through wire ingress during mailbox draining. Streamlines `DirectChatCoordinator` to resolve routes via sender's `IRoutingCoordinator`.

- **Files:**
  - `Modify`: `Percolator.Domain/Identities/ValueObjects/PortRange.cs`
  - `Modify`: `Percolator.Domain/Delivery/ValueObjects/RelayEndpoint.cs`
  - `Modify`: `Percolator.Infrastructure/Transport/PeerGrpcChannelFactory.cs`
  - `Modify`: `Percolator.Infrastructure/Transport/TransportDispatcher.cs`
  - `Modify`: `Percolator.Infrastructure/Transport/GrpcRelayMailboxProvisioner.cs`
  - `Modify`: `Percolator.Infrastructure/Transport/DiscoveryQueryClient.cs`
  - `Modify`: `Percolator.Infrastructure/Transport/Endpoints/NearbyDiscoveryEndpoint.cs`
  - `Modify`: `Percolator.Desktop/Common/Services/PortReservationService.cs`
  - `Modify`: `Percolator.Desktop/Features/Transports/ViewModels/AddConnectionDialogViewModel.cs`
  - `Modify`: `Percolator.Desktop/Features/Transports/Views/AddConnectionDialog.axaml`
  - `Modify`: `Percolator.Desktop/Features/Transports/Views/ShareInvitationDialog.axaml`
  - `Modify`: `Percolator.Simulator/Features/RelayedChat/SimulatedRelayHost.cs`
  - `Modify`: `Percolator.Simulator/Features/Topology/SimulatedPersonaHost.cs`
  - `Modify`: `Percolator.Simulator/Features/RelayedChat/RelayCardView.axaml`
  - `Modify`: `Percolator.Simulator/Features/RelayedChat/RelayCardViewModel.cs`
  - `Delete`: `Percolator.Simulator/Features/RelayedChat/RelayEndpointExportFlyoutViewModel.cs`
  - `Delete`: `Percolator.Simulator.Tests/RelayedChat/RelayEndpointExportFlyoutViewModelTests.cs`
  - `Modify`: `Percolator.Simulator/Features/RelayedChat/RelayProvisioningCoordinator.cs`
  - `Modify`: `Percolator.Simulator/Features/DirectChat/DirectChatCoordinator.cs`
  - `Modify`: `Percolator.Simulator/Features/Topology/PersonaCardView.axaml`
  - `Modify`: `Percolator.Simulator/Features/Topology/PersonaInvitationExportFlyoutViewModel.cs`
  - `Modify`: `Percolator.Simulator/Features/Topology/RelayRegistrationFlyoutViewModel.cs`
  - `Create`: `Percolator.Domain.Tests/Delivery/RelayEndpointTests.cs`
  - `Modify`: `Percolator.Domain.Tests/Identities/PortRangeTests.cs`
  - `Modify`: `Percolator.Infrastructure.Tests/Transport/Primitives/PeerGrpcChannelFactoryTests.cs`
  - `Modify`: `Percolator.Desktop.Tests/Features/Transports/AddConnectionDialogViewModelTests.cs`
  - `Modify`: `Percolator.Simulator.Tests/Topology/RelayRegistrationFlyoutViewModelTests.cs`

- **Technical Implementation Steps:**
  1. **Domain Port & Endpoint Refactor**:
     - In `PortRange.cs`: `DefaultDynamicRange => new(5100, 5199);`.
     - In `RelayEndpoint.cs`:
       - `FromHostAndPort(host, port)` formats `Value = $"percolator://{trimmedHost}:{port}"`.
       - `Create(endpoint)`: Validates that if `endpoint` contains `://`, the scheme must equal `"percolator"`. Rejects `relay://`, `https://`, and `http://` with `INVALID_RELAY_SCHEME`. If raw `host:port` is passed, normalizes to `percolator://{host}:{port}`.
       - Expose `public Uri ToUri() => new(Value);`.
  2. **Infrastructure Transport Adapter Translation**:
     - In `PeerGrpcChannelFactory.GetOrCreateChannel(Uri address)`:
       ```csharp
       var targetAddress = address.Scheme.Equals("percolator", StringComparison.OrdinalIgnoreCase)
           ? new UriBuilder(address) { Scheme = Uri.UriSchemeHttps }.Uri
           : address;
       ```
     - In `TransportDispatcher.ParseEndpointUri(string endpointStr)`: If starts with `percolator://`, returns `new Uri(endpointStr)`. If lacks scheme, returns `new Uri($"percolator://{endpointStr}")`.
     - In `GrpcRelayMailboxProvisioner.CreateClient`: Call `_channelFactory.GetOrCreateChannel(endpoint.ToUri())`.
     - In `DiscoveryQueryClient.ToUri`: Return `endpoint.ToUri()`.
     - In `NearbyDiscoveryEndpoint.cs`: Set `RelayEndpoint = $"percolator://127.0.0.1:{_boundPort}"`.
  3. **Desktop Realignment**:
     - In `PortReservationService.cs`: `DefaultRangeStart = 5100; DefaultRangeEnd = 5199;`.
     - In `AddConnectionDialogViewModel.cs`: Strip prefix `percolator://` on relay input; reject `relay://` with clear error message.
     - In `AddConnectionDialog.axaml`: Change label and watermark from `relay://` to `percolator://`.
     - In `ShareInvitationDialog.axaml`: Update default dynamic range text to `5100-5199`.
  4. **Simulator Host & UI Realignment**:
     - In `SimulatedRelayHost.cs`: Bind Kestrel to `IPAddress.Loopback` and initialize `BoundEndpoint = new Uri($"percolator://127.0.0.1:{BoundPort}");`.
     - In `SimulatedPersonaHost.cs`: Initialize `BoundEndpoint = new Uri($"percolator://127.0.0.1:{BoundPort}");`.
     - In `RelayCardView.axaml`: Replace `DropDownButton` with `<Button Content="📋 Copy Endpoint" Command="{Binding CopyEndpointCommand}" />`.
     - In `RelayCardViewModel.cs`: Implement `[RelayCommand] private async Task CopyEndpointAsync()` copying `percolator://127.0.0.1:{_host.BoundPort}`. Remove `EndpointExport`.
     - Delete `RelayEndpointExportFlyoutViewModel.cs` and `RelayEndpointExportFlyoutViewModelTests.cs`.
     - In `PersonaInvitationExportFlyoutViewModel.cs`: Lock host to `127.0.0.1` and default port range to `5100-5199`.
  5. **Coordinator Decoupling & Bypass Elimination**:
     - In `RelayProvisioningCoordinator.InitiateRelayedHandshakeAsync`:
       - Remove recipient DB writes (lines 335–346).
       - Remove manual `DirectChannel` and `PeerContact` creation (lines 365–390).
       - Replace with:
         ```csharp
         using var scope = initiator.Services.CreateScope();
         var connectionService = scope.ServiceProvider.GetRequiredService<IPeerConnectionService>();
         var result = await connectionService.ConnectToPeerAsync(target.PublicIdentityId, DeviceId.Primary, relayEndpoint, ct);
         if (result.IsFailure) { /* Toast and return false */ }
         await initiator.FlushOutboxAsync(ct);
         ```
     - In `DirectChatCoordinator.SendDirectMessageAsync`: Replace cross-host `recipient.HomeRelay` check with `sender.Services.GetRequiredService<IRoutingCoordinator>().ResolveRouteAsync(...)`.

- **Semantic Unit Tests:**
  - `RelayEndpointTests`:
    - `Create_WithPercolatorScheme_ReturnsNormalizedEndpoint`
    - `Create_WithDisallowedScheme_ReturnsInvalidSchemeError`
    - `Create_WithRawHostAndPort_NormalizesToPercolatorScheme`
  - `PortRangeTests`:
    - `DefaultDynamicRange_Is5100To5199`
  - `PeerGrpcChannelFactoryTests`:
    - `GetOrCreateChannel_WithPercolatorScheme_MapsToHttpsForUnderlyingSocket`
  - `AddConnectionDialogViewModelTests`:
    - `ConnectRelay_WithPercolatorScheme_NormalizesAndInitiates`
    - `ConnectRelay_WithDisallowedRelayScheme_SetsErrorMessage`
  - `RelayCardViewModelTests`:
    - `CopyEndpointCommand_CopiesPercolatorUriToClipboard`

- **Verify:**
  - `dotnet test Percolator.Domain.Tests`
  - `dotnet test Percolator.Infrastructure.Tests`
  - `dotnet test Percolator.Application.Tests`
  - `dotnet test Percolator.Desktop.Tests`
  - `dotnet test Percolator.Simulator.Tests`
  - `dotnet test Percolator.sln`
  - `dotnet run --project Percolator.Simulator --framework net10.0-windows10.0.19041.0 -- --replay Scenarios`
  - `pwsh scripts/verify-code-integrity.ps1`

- **QMS:** [`DEC-CANONICAL-PORT-RANGE`], [`DEC-CANONICAL-URI-SCHEME`], [`SYS-MUTUAL-AUTHENTICATED-WIRE-TRANSPORT`], [`HAZ-TRANSPORT-SPOOFING`], [`HAZ-INGRESS-RESOURCE-EXHAUSTION`]

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

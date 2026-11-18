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
| Simulator | `RelayEndpointExportFlyoutViewModel` | `Percolator.Simulator/Features/RelayedChat/RelayEndpointExportFlyoutViewModel.cs` | Transient |
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

### CONN-12b ⬜ Simulator Architecture Realignment: Ingress/Egress Wire Pipelines, Application Outbound Workflow & High-Density Card UI

- **Specification Governance:** Aligns the Simulator's connection mechanics with Desktop by promoting outbound connection orchestration to a shared Application-layer workflow (`IOutboundConnectionWorkflow`). Intercepts Ingress and Egress at canonical transport seams (`ITransportDispatcher` and `IInboundIngressPipeline`), enabling fault injection (chaos envelope drops, latency, corruption), autopilot outbox auto-flushing, and wire telemetry without touching production code or hacking SQLite repositories (`ChaosPendingIngressStoreDecorator`). Eliminates coordinator database backdoors: removes manual `PeerContact` insertions from `DirectHandshakeOrchestrator` and `DirectChannel` insertions from `DirectChatCoordinator.EnsureChannelAsync`. Preserves multi-node developer utility ergonomics: eliminates modal dialog lockout by retaining non-blocking, high-density Avalonia Flyouts (`🔗 Connect Peer ▼`, `📋 Share Invite ▼`, `📬 Relay ▼`) on the card toolbar, while tucking away 14 checkboxes/actions (Capabilities, Autopilot, PreKey publish, Reroll) into a unified `⚙️ Persona Settings ▼` header menu.

- **Files:**
  - `Create`: `Percolator.Application/Connections/OutboundConnectionStatus.cs`
  - `Create`: `Percolator.Application/Connections/OutboundConnectionResult.cs`
  - `Create`: `Percolator.Application/Connections/IOutboundConnectionWorkflow.cs`
  - `Create`: `Percolator.Application/Connections/OutboundConnectionWorkflow.cs`
  - `Create`: `Percolator.Simulator/Features/Chaos/SimulatorChaosTransportDispatcherDecorator.cs`
  - `Create`: `Percolator.Simulator/Features/Topology/RelayRegistrationFlyoutViewModel.cs`
  - `Modify`: `Percolator.Application/DependencyInjection/ApplicationServiceCollectionExtensions.cs` (register `IOutboundConnectionWorkflow` as Scoped)
  - `Modify`: `Percolator.Desktop/Features/Transports/ViewModels/AddConnectionDialogViewModel.cs` (delegate direct peer connection to `IOutboundConnectionWorkflow`)
  - `Modify`: `Percolator.Desktop/Features/Transports/Models/AddConnectionDialogModels.cs` (reference `Percolator.Application.Connections.OutboundConnectionStatus`)
  - `Modify`: `Percolator.Simulator/Features/Topology/SimulatedPersonaHost.cs` (decorate `ITransportDispatcher` with `SimulatorChaosTransportDispatcherDecorator`, decorate `IInboundIngressPipeline` with `SimulatorOutboxFlushingIngressPipelineDecorator`, remove `ChaosPendingIngressStoreDecorator`)
  - `Modify`: `Percolator.Simulator/Features/Topology/PersonaCardView.axaml` (realign header with `⚙️ Persona Settings ▼` menu; consolidate Row 1 into 3 compact flyout buttons: `🔗 Connect Peer ▼`, `📋 Share Invite ▼`, `📬 Relay ▼`)
  - `Modify`: `Percolator.Simulator/Features/Topology/PersonaCardViewModel.cs` (bind settings dropdown, wire flyout view models)
  - `Modify`: `Percolator.Simulator/Features/Topology/PersonaConnectPeerFlyoutViewModel.cs` (dispatch through `IOutboundConnectionWorkflow`, auto-fill payload from selected in-world peer)
  - `Modify`: `Percolator.Simulator/Features/Topology/PersonaInvitationExportFlyoutViewModel.cs` (add 1-click clipboard copy with port/host presets)
  - `Modify`: `Percolator.Simulator/Features/DirectChat/DirectHandshakeOrchestrator.cs` (dispatch outbound handshakes through `IOutboundConnectionWorkflow`, eliminate manual `PeerContact` DB insertion)
  - `Modify`: `Percolator.Simulator/Features/DirectChat/DirectChatCoordinator.cs` (eliminate `EnsureChannelAsync` manual DB insertion)
  - `Modify`: `Percolator.Simulator/Features/Scenarios/ScenarioRunnerService.cs` (invoke `IOutboundConnectionWorkflow` for scenario handshakes)
  - `Delete`: `Percolator.Simulator/Features/Topology/PeerConnectionTargetKind.cs`
  - `Delete`: `Percolator.Simulator/Features/Topology/SimulatedPeerConnectionService.cs`
  - `Delete`: `Percolator.Simulator/Features/Chaos/ChaosPendingIngressStoreDecorator.cs`
  - `Create`: `Percolator.Application.Tests/Connections/OutboundConnectionWorkflowTests.cs`
  - `Create`: `Percolator.Simulator.Tests/Chaos/SimulatorChaosTransportDispatcherDecoratorTests.cs`
  - `Modify`: `Percolator.Simulator.Tests/Topology/PersonaConnectPeerFlyoutViewModelTests.cs`
  - `Modify`: `Percolator.Simulator.Tests/Topology/PersonaInvitationExportFlyoutViewModelTests.cs`

- **Contracts & ViewModels:**
  ```csharp
  namespace Percolator.Application.Connections;

  public enum OutboundConnectionStatus
  {
      Connected = 0,
      PendingOffline = 1
  }

  public sealed record OutboundConnectionResult(
      OutboundConnectionStatus Status,
      PublicIdentityId PeerId,
      PeerInvitationPayload Invitation,
      Uri ResolvedEndpoint);

  public interface IOutboundConnectionWorkflow
  {
      Task<DomainResult<OutboundConnectionResult>> ConnectAsync(
          PeerInvitationPayload payload,
          TimeSpan probeTimeout,
          CancellationToken ct = default);
  }

  public sealed class OutboundConnectionWorkflow : IOutboundConnectionWorkflow
  {
      private readonly IPeerConnectionService _connectionService;
      private readonly ILogger<OutboundConnectionWorkflow> _logger;

      public OutboundConnectionWorkflow(
          IPeerConnectionService connectionService,
          ILogger<OutboundConnectionWorkflow> logger)
      {
          _connectionService = connectionService ?? throw new ArgumentNullException(nameof(connectionService));
          _logger = logger ?? throw new ArgumentNullException(nameof(logger));
      }

      public async Task<DomainResult<OutboundConnectionResult>> ConnectAsync(
          PeerInvitationPayload payload,
          TimeSpan probeTimeout,
          CancellationToken ct = default)
      {
          // 1. Resolve direct URI from payload.EndpointHosts and PortRange
          // 2. Execute _connectionService.ConnectToDirectPeerAsync with timeout cts
          // 3. If success: return OutboundConnectionResult(Connected, ...)
          // 4. If probe failure / timeout: return OutboundConnectionResult(PendingOffline, ...)
      }
  }

  namespace Percolator.Simulator.Features.Chaos;

  public sealed class SimulatorChaosTransportDispatcherDecorator : ITransportDispatcher
  {
      private readonly ITransportDispatcher _inner;
      private readonly ISimulatorChaosController _chaos;
      private readonly SimulatedNodeId _nodeId;
      private readonly ISimulatorLogSink _logSink;

      public SimulatorChaosTransportDispatcherDecorator(
          ITransportDispatcher inner,
          ISimulatorChaosController chaos,
          SimulatedNodeId nodeId,
          ISimulatorLogSink logSink)
      {
          _inner = inner ?? throw new ArgumentNullException(nameof(inner));
          _chaos = chaos ?? throw new ArgumentNullException(nameof(chaos));
          _nodeId = nodeId;
          _logSink = logSink ?? throw new ArgumentNullException(nameof(logSink));
      }

      public async ValueTask<DomainResult> SendAsync(OutboxJob job, CancellationToken ct = default)
      {
          var fault = _chaos.EvaluateDeliveryFault(_nodeId);
          if (fault == ChaosFaultAction.DropEnvelope)
          {
              _logSink.Log(new SimulatorLogEntry(DateTimeOffset.UtcNow, LogLevel.Warning, _nodeId.ToString(), _nodeId, "Chaos", "Outbound envelope dropped by chaos fault injection."));
              return DomainResult.Failure(new DomainError("CHAOS_DROPPED", "Envelope dropped by simulator chaos controller."));
          }
          if (fault == ChaosFaultAction.InjectArtificialDelay)
          {
              await Task.Delay(500, ct).ConfigureAwait(false);
          }
          return await _inner.SendAsync(job, ct).ConfigureAwait(false);
      }
  }

  namespace Percolator.Simulator.Features.Topology;

  public sealed partial class PersonaConnectPeerFlyoutViewModel : ViewModelBase
  {
      private readonly ISimulatedPersonaHost _host;
      private readonly ISimulatedWorldStateService _worldState;
      private readonly IPeerInvitationCodec _codec;
      private readonly IOutboundConnectionWorkflow _connectionWorkflow;

      [ObservableProperty] private int _selectedTabIndex;
      [ObservableProperty] private IReadOnlyList<SimulatedNodeSummary> _availableInWorldPersonas = [];
      [ObservableProperty] private SimulatedNodeSummary? _selectedInWorldPersona;
      [ObservableProperty] private string _externalInvitationInput = string.Empty;
      [ObservableProperty] private bool _isConnecting;
      [ObservableProperty] private string? _errorMessage;

      [RelayCommand] private Task ConnectAsync();
  }

  public sealed partial class RelayRegistrationFlyoutViewModel : ViewModelBase
  {
      private readonly ISimulatedPersonaHost _host;
      private readonly ISimulatedWorldStateService _worldState;
      private readonly IRelayMailboxProvisioner _relayProvisioner;

      [ObservableProperty] private int _selectedTabIndex;
      [ObservableProperty] private IReadOnlyList<SimulatedNodeSummary> _availableInWorldRelays = [];
      [ObservableProperty] private SimulatedNodeSummary? _selectedInWorldRelay;
      [ObservableProperty] private string _externalRelayInput = string.Empty;
      [ObservableProperty] private bool _seedPreKeysToRelay;
      [ObservableProperty] private bool _isRegistering;
      [ObservableProperty] private string? _errorMessage;

      [RelayCommand] private Task RegisterRelayAsync();
  }

  public sealed partial class PersonaInvitationExportFlyoutViewModel : ViewModelBase
  {
      private readonly ISimulatedPersonaHost _host;
      private readonly IPeerInvitationCodec _codec;
      private readonly IClipboardService _clipboard;

      [ObservableProperty] private SimulatorAdvertisedHostKind _selectedHostKind;
      [ObservableProperty] private string _detectedLanIp = string.Empty;
      [ObservableProperty] private string _customHostInput = string.Empty;
      [ObservableProperty] private PortRangePrivacyPreset _selectedPortPreset;
      [ObservableProperty] private int _customPortMin = 5100;
      [ObservableProperty] private int _customPortMax = 5120;
      [ObservableProperty] private string _generatedInvitationUrl = string.Empty;

      [RelayCommand] private Task CopyInvitationUrlAsync();
      [RelayCommand] private void UpdateGeneratedInvitation();
  }
  ```

- **Algorithm:**
  1. **Promote Application Outbound Connection Workflow:** Create `IOutboundConnectionWorkflow` and `OutboundConnectionWorkflow` in `Percolator.Application/Connections/`. Consolidates host candidate resolution, defensive `UriBuilder` parsing, 3-second probing timeout, and fallback staging as `OutboundConnectionStatus.PendingOffline`. Registers in DI as `services.AddScoped<IOutboundConnectionWorkflow, OutboundConnectionWorkflow>()`.
  2. **Refactor Desktop Add Connection Dialog:** Update `AddConnectionDialogViewModel` to delegate direct peer probing to `IOutboundConnectionWorkflow`, eliminating duplicate probing and URI resolution logic.
  3. **Egress Pipeline Decoration (Non-Invasive Network Chaos):** Create `SimulatorChaosTransportDispatcherDecorator` implementing `ITransportDispatcher`. Intercepts `SendAsync(job)` in `SimulatedPersonaHost`: drops frames on `DropEnvelope`, delays on `InjectArtificialDelay`, and logs wire activity to `ISimulatorLogSink`. Deletes `ChaosPendingIngressStoreDecorator`, removing persistence-layer SQLite store hacking.
  4. **Ingress Pipeline Decoration (Non-Invasive Ingress Control):** Register `SimulatorOutboxFlushingIngressPipelineDecorator` on `IInboundIngressPipeline` in `SimulatedPersonaHost`. Ingress envelopes auto-flush outbox responses and log wire traffic naturally.
  5. **Eliminate Coordinator Database Backdoors:**
     - Remove manual `PeerContact` construction and DB saving from `DirectHandshakeOrchestrator.InitiateDirectHandshakeAsync`. Dispatches handshake through `IOutboundConnectionWorkflow` and flushes outbox; contact creation occurs naturally via the wire handshake pipeline.
     - Remove `EnsureChannelAsync` from `DirectChatCoordinator`. Messaging operations rely on standard wire channels and ingress discovery.
     - Remove `PeerConnectionTargetKind.cs` and `SimulatedPeerConnectionService.cs`. Selecting an in-world persona simply reads its public tokens to auto-fill `PeerInvitationPayload`.
  6. **High-Density Utility UI Consolidation in `PersonaCardView`:**
     - Consolidate Capabilities (4 checkboxes), Autopilot (7 checkboxes), `Publish PreKeys`, and `Reroll Profile` into a single `⚙️ Persona Settings ▼` dropdown menu in the card header beside `🟢`.
     - Consolidate the Row 1 action toolbar into exactly 3 compact buttons with non-blocking inline flyouts: `🔗 Connect Peer ▼`, `📋 Share Invite ▼`, and `📬 Relay ▼`. Zero modal dialogs blocking the workbench.

- **Verify:**
  - `Percolator.Application.Tests/Connections/OutboundConnectionWorkflowTests.cs`:
    - `ConnectAsync_WithReachablePeer_ReturnsConnectedStatus`
    - `ConnectAsync_WhenProbeTimesOut_ReturnsPendingOfflineStatus`
    - `ConnectAsync_WithInvalidHostOrPort_ReturnsDomainError`
  - `Percolator.Simulator.Tests/Chaos/SimulatorChaosTransportDispatcherDecoratorTests.cs`:
    - `SendAsync_WhenFaultActionPass_DelegatesToInnerDispatcher`
    - `SendAsync_WhenFaultActionDropEnvelope_ReturnsFailureWithoutCallingInner`
    - `SendAsync_WhenFaultActionDelay_DelaysExecution`
  - `Percolator.Simulator.Tests/Topology/PersonaConnectPeerFlyoutViewModelTests.cs`:
    - `ConnectAsync_InWorldSelection_SynthesizesPayloadAndDispatchesWorkflow`
    - `ConnectAsync_ExternalCode_DecodesUrlAndDispatchesWorkflow`
  - `Percolator.Simulator.Tests/Topology/PersonaInvitationExportFlyoutViewModelTests.cs`:
    - `UpdateGeneratedInvitation_WithAutoLan_GeneratesValidUrl`
  - Command: `dotnet test Percolator.Application.Tests --filter "FullyQualifiedName~OutboundConnectionWorkflow"`
  - Command: `dotnet test Percolator.Simulator.Tests --filter "FullyQualifiedName~Simulator"`
  - Command: `dotnet run --project Percolator.Simulator --framework net10.0-windows10.0.19041.0 -- --replay Scenarios`
  - Command: `dotnet test Percolator.sln`

- **QMS:** [`SYS-CONTACT-CONSENT-GATE`], [`SYS-PRE-ACTIVATION-STEALTH`], [`SYS-BLOCK-WIRE-INDISTINGUISHABILITY`], [`SYS-MUTUAL-AUTHENTICATED-WIRE-TRANSPORT`], [`DEP-DESKTOP-OUT-OF-BAND-VERIFICATION`], [`HAZ-TRANSPORT-SPOOFING`], [`HAZ-PREKEY-STARVATION`]

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

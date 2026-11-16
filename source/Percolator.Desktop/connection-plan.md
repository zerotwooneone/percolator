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
| Simulator | `SimulatorAdvertisedHostKind` | `Percolator.Simulator/Features/Topology/SimulatorAdvertisedHostKind.cs` | Transient (Enum) |
| Simulator | `PeerConnectionTargetKind` | `Percolator.Simulator/Features/Topology/PeerConnectionTargetKind.cs` | Transient (Enum) |
| Simulator | `PersonaInvitationExportFlyoutViewModel` | `Percolator.Simulator/Features/Topology/PersonaInvitationExportFlyoutViewModel.cs` | Transient |
| Simulator | `PersonaConnectPeerFlyoutViewModel` | `Percolator.Simulator/Features/Topology/PersonaConnectPeerFlyoutViewModel.cs` | Transient |
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

### CONN-11 ⬜ Desktop Shell Realignment & Navigation Rail Dialog Orchestration (`shell-flow.md`)

- **Specification Governance:** Strictly enforces layout, ordering, and privacy invariants from `Percolator.Desktop/shell-flow.md`. Wires the Left Sidebar navigation rail to open `AddConnectionDialog`, `PendingRequestsPanelWindow`, and `ShareInvitationDialog` via `IWindowManager.ShowDialogAsync`.

- **Files:**
  - `Create`: `Percolator.Desktop/Features/Transports/Views/PendingRequestsPanelWindow.axaml` & `Percolator.Desktop/Features/Transports/Views/PendingRequestsPanelWindow.axaml.cs`
  - `Modify`: `Percolator.Desktop/Features/Shell/Views/MainWindow.axaml` (~L45-85, realign Left Sidebar Grid rows per `shell-flow.md`, bind `OpenAddConnectionCommand`, `OpenPendingRequestsCommand`, `OpenShareInvitationCommand`)
  - `Modify`: `Percolator.Desktop/Features/Shell/ViewModels/MainWindowViewModel.cs` (inject `IWindowManager`, `IPeerContactQueryService`, `IActiveIdentitySet`; add commands; bind `PendingRequestCount`)
  - `Modify`: `Percolator.Desktop/Common/Session/DesktopSessionServiceCollectionExtensions.cs` (register `MainWindowViewModel` with scoped session dependencies)

- **Contracts & ViewModel:**
  ```csharp
  namespace Percolator.Desktop.Features.Shell.ViewModels;

  public sealed partial class MainWindowViewModel : ViewModelBase
  {
      private readonly IWindowManager _windowManager;
      private readonly IPeerContactQueryService _contactQueryService;
      private readonly IActiveIdentitySet _activeIdentitySet;

      [ObservableProperty] private bool _isInspectorOpen;
      [ObservableProperty] private bool _isChatsSidebarOpen = true;
      [ObservableProperty] private int _pendingRequestCount;
      [ObservableProperty] private string _activeConversationTitle = string.Empty;

      public bool HasPendingRequests => PendingRequestCount > 0;

      public MainWindowViewModel(
          IWindowManager windowManager,
          IPeerContactQueryService contactQueryService,
          IActiveIdentitySet activeIdentitySet)
      {
          _windowManager = windowManager ?? throw new ArgumentNullException(nameof(windowManager));
          _contactQueryService = contactQueryService ?? throw new ArgumentNullException(nameof(contactQueryService));
          _activeIdentitySet = activeIdentitySet ?? throw new ArgumentNullException(nameof(activeIdentitySet));
      }

      [RelayCommand]
      private async Task OpenAddConnectionAsync()
      {
          await _windowManager.ShowDialogAsync<AddConnectionDialogViewModel, OutboundConnectionStatus>();
      }

      [RelayCommand]
      private async Task OpenPendingRequestsAsync()
      {
          await _windowManager.ShowDialogAsync<PendingRequestsPanelViewModel, object>();
          await RefreshPendingCountAsync();
      }

      [RelayCommand]
      private async Task OpenShareInvitationAsync()
      {
          await _windowManager.ShowDialogAsync<ShareInvitationDialogViewModel, object>();
      }

      [RelayCommand]
      private void ToggleInspector() => IsInspectorOpen = !IsInspectorOpen;

      [RelayCommand]
      private void ToggleChatsSidebar() => IsChatsSidebarOpen = !IsChatsSidebarOpen;

      [RelayCommand]
      private async Task RefreshPendingCountAsync()
      {
          var activePersona = _activeIdentitySet.PrimaryActiveIdentity;
          if (activePersona == null) return;
          var contacts = await _contactQueryService.GetContactSummariesAsync(activePersona.Id);
          PendingRequestCount = contacts.Count(c => c.State == ContactState.PendingApproval);
      }
  }
  ```

- **Algorithm:**
  1. **PendingRequestsPanelWindow Creation:**
     - Creates `PendingRequestsPanelWindow.axaml` (Window) hosting `<views:PendingRequestsPanelView />`. Matches `WindowManager.ResolveWindowByConvention` naming convention for `PendingRequestsPanelViewModel`.
     - Window properties: `Title="Pending Connection Requests"`, `Width="480"`, `Height="560"`, `SizeToContent="Manual"`, `CanResize="False"`, `WindowStartupLocation="CenterOwner"`.
  2. **Left Sidebar Realignment (`shell-flow.md`):**
     - Left sidebar `Grid` configured with `RowDefinitions="Auto,*,Auto,Auto,Auto"`:
       - Row 0 (Top Zone): `[Direct Messages]` button with chat bubble icon (`ui:SymbolIcon Symbol="Chat"`), tooltip *"Direct Messages"*. Bound to `SelectDirectMessagesCommand` (switches conversation list to DMs).
       - Row 1 (Middle Zone): Connected Relays list (displays connected relay icons, or empty state placeholder *"Add a relay to discover public groups and post offices"*).
       - Row 2 (Bottom Zone, Top): `+` ("Add Connection") button anchored strictly above `[Pending Requests]`. Bound to `OpenAddConnectionCommand`.
       - Row 3 (Bottom Zone, Middle): `[Pending Requests]` button with silent notification indicator badge. Anchored strictly above Avatar per `shell-flow.md` to prevent spam from distracting the user (`INV-UNSOLICITED-CONNECTION-SILENCE`). Bound to `OpenPendingRequestsCommand`.
       - Row 4 (Bottom Zone, Bottom): User Avatar and compact display name. Bound to `OpenShareInvitationCommand`.
  3. **Inbound Consent Gate & Privacy Silence:**
     - Handshake offers arrive silently without OS popups or toasts (`INV-UNSOLICITED-CONNECTION-SILENCE`).
     - `MainWindowViewModel.RefreshPendingCountAsync` queries `IPeerContactQueryService` to illuminate the sidebar badge.
     - Clicking `[Pending Requests]` opens `PendingRequestsPanelWindow` via `_windowManager.ShowDialogAsync`, allowing the user to Accept, Reject, or Block.

- **Verify:**
  - `Percolator.Desktop.Tests/Features/Shell/MainWindowViewModelTests.cs`:
    - `OpenAddConnectionCommand_OpensAddConnectionDialogViaWindowManager`
    - `OpenPendingRequestsCommand_OpensPendingRequestsPanelWindowAndRefreshesCount`
    - `OpenShareInvitationCommand_OpensShareInvitationDialogViaWindowManager`
    - `RefreshPendingCountAsync_WithPendingApprovals_UpdatesCountAndHasPendingFlag`
    - `RefreshPendingCountAsync_WithNoPending_HasPendingRequestsIsFalse`
    - `ToggleInspector_InvertsIsInspectorOpenState`
    - `ToggleChatsSidebar_InvertsIsChatsSidebarOpenState`
  - Command: `dotnet test Percolator.Desktop.Tests --filter "FullyQualifiedName~MainWindowViewModel"`

- **QMS:** [`DEP-DESKTOP-UNSOLICITED-CONSENT-GATE`], [`INV-UNSOLICITED-CONNECTION-SILENCE`], [`SYS-CONTACT-CONSENT-GATE`], [`SYS-BLOCK-WIRE-INDISTINGUISHABILITY`], [`HAZ-UNSOLICITED-WIRE-SPAM`]

---

### CONN-12 ⬜ Simulator Dual-Mode Peer & Relay Interoperability (In-World & External with IP Override)

- **Specification Governance:** Refactors the Simulator topology UI so that personas and relays can seamlessly interoperate both internally (between simulated nodes in the same world) and externally (across real TCP/TLS sockets to `Percolator.Desktop` or another network host), with customizable IP address and port configuration.

- **Files:**
  - `Create`: `Percolator.Simulator/Features/Topology/SimulatorAdvertisedHostKind.cs`
  - `Create`: `Percolator.Simulator/Features/Topology/PeerConnectionTargetKind.cs`
  - `Create`: `Percolator.Simulator/Features/Topology/PersonaInvitationExportFlyoutViewModel.cs`
  - `Create`: `Percolator.Simulator/Features/Topology/PersonaConnectPeerFlyoutViewModel.cs`
  - `Create`: `Percolator.Simulator/Features/RelayedChat/RelayEndpointExportFlyoutViewModel.cs`
  - `Modify`: `Percolator.Simulator/Features/Topology/PersonaCardView.axaml` & `PersonaCardView.axaml.cs` (~L68, add "📋 Share Invite" and "🔗 Connect Peer" flyout buttons)
  - `Modify`: `Percolator.Simulator/Features/Topology/PersonaCardViewModel.cs` (bind invitation export, peer connection, and relay registration flyouts)
  - `Modify`: `Percolator.Simulator/Features/RelayedChat/RelayCardView.axaml` & `RelayCardView.axaml.cs` (add "📋 Copy Endpoint" flyout button)
  - `Modify`: `Percolator.Simulator/Features/RelayedChat/RelayCardViewModel.cs` (bind endpoint export flyout)

- **Contracts & ViewModels:**
  ```csharp
  namespace Percolator.Simulator.Features.Topology;

  public enum SimulatorAdvertisedHostKind
  {
      AutoLanIp = 0,
      Loopback = 1,
      Custom = 2
  }

  public enum PeerConnectionTargetKind
  {
      InWorldSimulatedNode = 0,
      ExternalInvitationCode = 1
  }

  public sealed partial class PersonaInvitationExportFlyoutViewModel : ViewModelBase
  {
      private readonly ISimulatedPersonaHost _host;
      private readonly IPeerInvitationCodec _codec;

      [ObservableProperty] private SimulatorAdvertisedHostKind _selectedHostKind;
      [ObservableProperty] private string _detectedLanIp = string.Empty;
      [ObservableProperty] private string _customHostInput = string.Empty;
      [ObservableProperty] private PortRangePrivacyPreset _selectedPortPreset;
      [ObservableProperty] private int _customPortMin = 5100;
      [ObservableProperty] private int _customPortMax = 5120;
      [ObservableProperty] private string _generatedInvitationUrl = string.Empty;

      public PersonaInvitationExportFlyoutViewModel(
          ISimulatedPersonaHost host,
          IPeerInvitationCodec codec)
      {
          _host = host ?? throw new ArgumentNullException(nameof(host));
          _codec = codec ?? throw new ArgumentNullException(nameof(codec));
      }

      [RelayCommand] private Task CopyInvitationUrlAsync();
      [RelayCommand] private void UpdateGeneratedInvitation();
  }

  public sealed partial class PersonaConnectPeerFlyoutViewModel : ViewModelBase
  {
      private readonly ISimulatedPersonaHost _host;
      private readonly ISimulatedWorldStateService _worldState;
      private readonly IPeerInvitationCodec _codec;
      private readonly IPeerConnectionService _peerConnectionService;

      [ObservableProperty] private PeerConnectionTargetKind _targetKind;
      [ObservableProperty] private IReadOnlyList<SimulatedNodeSummary> _availableInWorldPersonas = [];
      [ObservableProperty] private SimulatedNodeSummary? _selectedInWorldTarget;
      [ObservableProperty] private string _externalInvitationInput = string.Empty;
      [ObservableProperty] private bool _isConnecting;
      [ObservableProperty] private string? _errorMessage;

      public PersonaConnectPeerFlyoutViewModel(
          ISimulatedPersonaHost host,
          ISimulatedWorldStateService worldState,
          IPeerInvitationCodec codec,
          IPeerConnectionService peerConnectionService)
      {
          _host = host ?? throw new ArgumentNullException(nameof(host));
          _worldState = worldState ?? throw new ArgumentNullException(nameof(worldState));
          _codec = codec ?? throw new ArgumentNullException(nameof(codec));
          _peerConnectionService = peerConnectionService ?? throw new ArgumentNullException(nameof(peerConnectionService));
      }

      [RelayCommand] private Task ConnectAsync();
  }

  public sealed partial class RelayEndpointExportFlyoutViewModel : ViewModelBase
  {
      private readonly ISimulatedRelayHost _relayHost;

      [ObservableProperty] private SimulatorAdvertisedHostKind _selectedHostKind;
      [ObservableProperty] private string _detectedLanIp = string.Empty;
      [ObservableProperty] private string _customHostInput = string.Empty;
      [ObservableProperty] private string _generatedRelayUrl = string.Empty;

      public RelayEndpointExportFlyoutViewModel(ISimulatedRelayHost relayHost)
      {
          _relayHost = relayHost ?? throw new ArgumentNullException(nameof(relayHost));
      }

      [RelayCommand] private Task CopyRelayUrlAsync();
      [RelayCommand] private void UpdateGeneratedRelayUrl();
  }
  ```

- **Algorithm:**
  1. **Persona Card: Share / Export Invitation Button:**
     - Renders a DropDownButton with Flyout on `PersonaCardView`:
       - Discovers all candidate host addresses: primary LAN IP (from network interfaces), `127.0.0.1` (loopback), and custom IP input field.
       - Configures port range mode: Default dynamic (`5100-5299`), custom range, or bound single port.
       - Generates `percolator://invite?...` using `_codec.Encode(payload)`.
       - One-click copy button copies the URL to the system clipboard.
  2. **Persona Card: Connect to Peer Button:**
     - Renders a DropDownButton with Flyout on `PersonaCardView`:
       - **Mode 0 (In-World Simulated Node):** Dropdown listing all other active simulated personas in the world (filtered to exclude self). Selecting a persona extracts the remote peer ID, tokens, and bound endpoint from `_worldState.GetPersonaHost(targetId)`, then initiates direct handshake via `_peerConnectionService.ConnectToDirectPeerAsync`. Enforces Observer Isolation (Anti-Shortcut Rule 2): never accesses target's DI container or private storage.
       - **Mode 1 (External Node / Desktop):** Input text box accepting a `percolator://invite?...` URL or base64 code. Automatically cleanses scheme prefix, decodes payload via `_codec.Decode`, and calls `_peerConnectionService.ConnectToDirectPeerAsync`.
  3. **Persona Card: Register Relay Button:**
     - Extends existing button with a Flyout allowing selection of either an in-world simulated relay host OR an external relay URL (`https://<host>:<port>`).
  4. **Relay Card: Copy Endpoint Button:**
     - Renders a DropDownButton with Flyout on `RelayCardView` allowing host selection (Auto-detected LAN IP, `127.0.0.1`, or Custom Host/IP) and copies the normalized relay URL (`https://<host>:<port>`) to clipboard for direct entry into Desktop's Add Relay tab.

- **Verify:**
  - `Percolator.Simulator.Tests/Topology/PersonaInvitationExportFlyoutViewModelTests.cs`:
    - `UpdateGeneratedInvitation_WithAutoLan_EncodesLanEndpointInUrl`
    - `UpdateGeneratedInvitation_WithLoopback_EncodesLoopbackInUrl`
    - `UpdateGeneratedInvitation_WithCustomHost_EncodesCustomHostInUrl`
    - `UpdateGeneratedInvitation_WithPortPresetDefault_OmitsPortRange`
    - `UpdateGeneratedInvitation_WithPortPresetCustom_EncodesPortRange`
  - `Percolator.Simulator.Tests/Topology/PersonaConnectPeerFlyoutViewModelTests.cs`:
    - `ConnectAsync_InWorldMode_ResolvesTargetFromWorldStateAndInitiatesHandshake`
    - `ConnectAsync_ExternalMode_CleansesSchemeAndDecodesUrl`
    - `ConnectAsync_WithInvalidUrl_SetsErrorMessageWithoutCrashing`
  - `Percolator.Simulator.Tests/RelayedChat/RelayEndpointExportFlyoutViewModelTests.cs`:
    - `UpdateGeneratedRelayUrl_WithAutoLan_GeneratesCorrectHttpsUrl`
    - `UpdateGeneratedRelayUrl_WithLoopback_GeneratesLoopbackUrl`
  - Command: `dotnet test Percolator.Simulator.Tests --filter "FullyQualifiedName~FlyoutViewModel"`
  - Command: `dotnet run --project Percolator.Simulator --framework net10.0-windows10.0.19041.0 -- --replay Scenarios`

- **QMS:** [`SYS-CONTACT-CONSENT-GATE`], [`SYS-PRE-ACTIVATION-STEALTH`], [`DEP-DESKTOP-OUT-OF-BAND-VERIFICATION`], [`SYS-DISCOVERY-PREKEY-ISOLATION`], [`HAZ-METADATA-IP-LEAKAGE`], [`HAZ-TRANSPORT-SPOOFING`]

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

# Simulator overhaul (actionable plan)

This document defines an *implementable* plan for overhauling the WPF simulator.

## Requirements checklist (must be supported)

The simulator must support all of the following scenarios and UI actions:

### Peer management
- Add/remove simulated peers.
- Toggle peer online/offline.
- Persist simulator state (peers + configuration) across runs.

### Handshake simulation (must cover both directions)

The simulator must be able to simulate sending and receiving handshake requests for:

- **Signal / standard X3DH flow** (“signal”)
  - Simulated peer initiates a standard session with main node.
  - Main node initiates a standard session with simulated peer.

- **Reverse-signal flow**
  - Simulated peer invites main node to initiate.
  - Main node invites simulated peer to initiate.

For each of the above, the simulator must support both:
- **Direct** delivery (gRPC-like ingress, network-free)
- **Relayed** delivery (opaque bytes via relay, network-free)

### Relay-hosted pre-key store simulation (required for standard Signal/X3DH)

The simulator must support pre-key bundle storage and lookup as part of **relay-capable simulated peers**:

- Any simulated peer may be marked as **relay-capable**.
- A relay-capable simulated peer hosts a pre-key bundle store keyed by recipient PKH.
- The pre-key store can hold bundles for:
  - other simulated peers
  - the main node
- The simulator UI must allow copying a simulated peer’s lookup key (PKH) so the main node can initiate a standard handshake with that simulated peer.

### Accept / reject
- Main node can approve or reject incoming handshake requests.
- Simulated peer can accept or reject incoming handshake requests.

### Relay simulation
- Simulate a dumb relay as a queue of opaque bytes keyed only by recipient routing key.
- Do not persist or attach sender identity to relay queue items.

---

## Simulator transport model

- Baseline model: **network-free driver + observer**.
  - Simulated peers deliver inbound messages to the main node by calling the same ingress as the gRPC endpoints.
  - The simulator uses outbound wiretap only for observation.

- Additionally (required for “main node initiates → simulated peer receives”): a **transport plug-in** exists in the simulator runtime to intercept a subset of outbound sends and route them to simulated peers.
  - No fake gRPC servers.
  - Does not affect non-simulator execution.

## Current constraints / ground truth (must match code)

- Reverse-signal invite ingress:
  - gRPC service method:
    - `Percolator.Application/Network/PercolatorMessageService.EstablishDirectSession(EstablishDirectSessionRequest, ServerCallContext)`
    - This calls `IEstablishDirectSessionService.QueueInviteAsync(... isRelayed: false ...)`.
  - For relayed injection, `ProcessRelayedOpaquePayloadCommand` calls:
    - `IEstablishDirectSessionService.QueueInviteAsync(... isRelayed: true ...)`
- Relay intake ingress:
  - `ProcessRelayedOpaquePayloadCommand` can parse:
    - ratchet ciphertext -> `InternalEnvelope`
    - relayed `EstablishDirectSessionRequest`
    - relayed `InviteHandshakeResponse`

- Standard (non-reverse) handshake protocol artifacts already present:
  - Protobuf RPC request/response types exist:
    - `Percolator.Contracts/Protos/messaging.proto`:
      - `EstablishSessionRequest`
      - `EstablishSessionResponse`
  - Protobuf initiator bootstrap message exists:
    - `Percolator.Contracts/Protos/internal_messaging.proto`:
      - `HandshakeInitiatorHello`
  - Initiator-side finalize path exists (responder’s first ratchet message):
    - `Percolator.Application/Network/Handshake/HandleHandshakeResponderHelloCommand.cs`
    - `Percolator.Application/Network/Handshake/InitiatorFinalizeService.cs`
  - A standard-handshake service stub exists:
    - `Percolator.Application/Services/IHandshakeService.cs`
    - `Percolator.Application/Services/HandshakeService.cs` (currently minimal/stub)

- Standard (non-reverse) handshake ingress is NOT currently wired:
  - `Percolator.Application/Network/PercolatorMessageService.cs` implements:
    - `EstablishDirectSession` (reverse-signal invite)
    - `DeliverInviteHandshakeResponse` (reverse-signal response)
    - `DeliverOpaqueMessage` (post-session + relay wrapper)
  - It does not currently implement `EstablishSession`.
  - No Application handler/ingress currently parses `HandshakeInitiatorHello` on inbound.

- Pre-key exchange protobufs already exist (used over an established session today):
  - `Percolator.Contracts/Protos/internal_messaging.proto`
    - `PrekeyEnvelope`
    - `SubmitPreKeyBundleRequest` / `SubmitPreKeyBundleResponse`
    - `GetPreKeyBundleRequest` / `GetPreKeyBundleResponse`
    - `GetPreKeyBundleRequest` lookup key is `public_key_hash` (SHA-256 of recipient identity signing public key SPKI)
  - Reference handlers:
    - `Percolator.Prekey/Handlers/SubmitPreKeyBundleHandler.cs`
    - `Percolator.Prekey/Handlers/GetPreKeyBundleHandler.cs`

## Glossary

- **Main node**: the running desktop app instance.
- **Simulated peer**: a test actor represented in the simulator UI. It does not run its own node process.
- **Signal / standard X3DH**: the initiator sends the initial handshake message to the acceptor, and the acceptor responds with their first ratchet message.
- **Reverse-signal**: the inviter sends a signed invitation which prompts the acceptor to initiate.
- **Pre-key store (relay-hosted)**: a store hosted by a relay-capable simulated peer where bundles are published and fetched by an out-of-band lookup key (PKH).

---


# Chunk F — Conversations and group creation (real persistence)

## Goal
Create and persist conversations/groups driven by simulator actions.

## Work
Use existing persistence and commands:
- `Percolator.Chat.IConversationRepository`
- `Percolator.Application.Apps.Chat.CreateGroupConversationCommand`

## Done when
- Simulator can create a group conversation from selected established peers and it persists.

# Chunk G — Replace IChatHistory with Chat Domain interface
- find the appropriate chat domain interface, create one if needed
- replace references to IChatHistory with the domain interface
- delete IChatHistory (cutover, no migration) and implementations. Ask the user to delete the files
- implement sqlite implementation of IGroupSenderKeyRepository and delete (cutover, no migration) InMemoryGroupSenderKeyRepository. Ask the user to delete this file

---

# Chunk H — Simulator window shell + navigation (match design screenshots)

## Design references
- `Desktop.Wpf/design/simulator.peers.png`
- `Desktop.Wpf/design/simulator.handshakes.png`
- `Desktop.Wpf/design/simulator.relay.png`
- `Desktop.Wpf/design/simulator.diag.png`

## Spec reference
- `Desktop.Wpf/simulator-flows.md`
  - **Information Architecture** (Peers / Handshakes / Relay / Sessions / Diagnostics)
  - **R4 — Separation of Concerns**

## Goal
Provide a single simulator window with a left-nav and consistent page layout so the rest of the chunks can plug into it.

## Work
- Update/replace the simulator host window to match the layout in the screenshots:
  - left navigation rail
  - top title bar “Protocol Simulator Environment”
  - main content area per tab

Target files:
- `Desktop.Wpf/Features/Simulator/HandshakeSimulatorWindow.xaml`
- `Desktop.Wpf/Features/Simulator/HandshakeSimulatorWindow.xaml.cs`
- `Desktop.Wpf/Features/Simulator/HandshakeSimulatorViewModel.cs`
- (likely) `Desktop.Wpf/Shared/*` for shared controls/styles

Implementation requirements:
- A single `SelectedTab` state that swaps views.
- Each tab is its own view + view model pair so later chunks can be implemented independently.
- A single “overflow” menu (top right `...` in screenshots) to host:
  - Reset simulator state
  - Export diagnostic bundle (placeholder is fine)

## Done when
- The simulator window matches the overall navigation/layout of the screenshots.
- Placeholder pages exist for all 5 tabs.

---

# Chunk I — Peers tab: peer cards, identity copy, and pre-key hosting/publishing graph

## Design references
- `Desktop.Wpf/design/simulator.peers.png`

## Spec reference
- `Desktop.Wpf/simulator-flows.md`
  - Flow 1 (Manage Simulated Peers)
  - Flow 2 (Many-to-Many Pre-Key Distribution)

## Goal
Implement the Peers tab as an interactive set of peer “cards” with:
- identity + PKH display and copy
- online/offline toggle
- relay enable toggle
- pre-key publish relationships (many-to-many) with revoke
- (optional) inline “send message to main app” input when handshake is complete

## Work
UI (Peers page):
- Implement list of peer cards similar to the screenshot:
  - name
  - PKH (truncated display) + copy button
  - “Enable Relay” checkbox
  - power toggle (online/offline)
  - optional badge when peer is relay-capable (e.g., “RELAY NODE”)
- Add “Add Peer” button (top right of page).

Pre-key publish graph:
- For each peer card, provide a “Publish keys to…” affordance:
  - dropdown of other peers (preferably relay-capable peers, but spec says many-to-many)
  - allow publishing to multiple peers (accumulate relationships)
- Render relationships as removable tags:
  - On publisher (A): `Published to: B` tags
  - On host (B): `Hosting keys for: A` tags
  - Clicking `X` removes just that relationship.

Pre-key options:
- When publishing, support:
  - include one-time pre-keys (On/Off)
  - count (when On)

Target files:
- `Desktop.Wpf/Features/Simulator/HandshakeSimulatorWindow.xaml`
- `Desktop.Wpf/Features/Simulator/HandshakeSimulatorViewModel.cs`
- `Desktop.Wpf/Features/Simulator/SimulatedPeerItemViewModel.cs`
- `Desktop.Wpf/Features/Simulator/SimulatorStateService.cs`
- `Desktop.Wpf/Features/Simulator/SimulatorStateService.*` (wherever publish relationships are stored)

Data model additions (if missing):
- Persist relationships:
  - `PublishedKeysToPeerIds: List<Guid>` on `SimulatedPeerDto`
  - or a separate adjacency list in `SimulatorStateDto` (either is fine)

## Done when
- You can add peers, toggle online, mark relay-capable.
- You can publish Peer A’s keys to Peer B and see tags on both sides.
- You can revoke a single relationship via `X`.

---

# Chunk J — Handshakes tab: explicit per-peer handshake state machines + manual progression

## Design references
- `Desktop.Wpf/design/simulator.handshakes.png`

## Spec reference
- `Desktop.Wpf/simulator-flows.md`
  - Flow 3 (Handshake Path A)
  - Flow 4 (Handshake Path B)
  - Flow 5 (OOB Token)
  - Flow 6 (Handshake State Machine Management)
  - **R2 — Explicit State Machines**

## Goal
Implement the Handshakes tab as a per-peer state machine dashboard with:
- visible state badges
- manual actions to advance the handshake
- state reset/force-expire controls

## Work
UI (Handshakes page):
- Render one “handshake state machine card” per peer (as in screenshot):
  - peer name
  - state badge (No Handshake / Request Sent / Request Received / Handshake Complete / Expired)
  - action buttons based on state:
    - No Handshake: “Send Request to Main App” (peer-initiated path)
    - Request Received: “Accept Handshake” (+ optional Reject)
    - Request Sent: “Force Expire”
    - Handshake Complete / Expired: “Reset State”

Behavior requirements:
- Manual-first: handshake must not complete without visible user actions unless explicitly enabled.
- Add per-peer toggles (nice-to-have) for:
  - Auto-accept handshake requests
  - Auto-respond / auto-send accept payload
  - These toggles must be visible on the card so the user understands why the state advanced.

Target files:
- `Desktop.Wpf/Features/Simulator/HandshakeSimulatorViewModel.cs`
- `Desktop.Wpf/Features/Simulator/SimulatedPeerItemViewModel.cs`
- `Desktop.Wpf/Features/Simulator/SimulatedPeerRuntimeService.cs` (or equivalent runtime handler)

## Done when
- Handshake states are visible and driven by explicit user actions.
- “Send Request to Main App” produces a relay queue item (see Chunk K).

---

# Chunk K — Relay tab: per-relay queues, auto-delivery controls, and fault injection

## Design references
- `Desktop.Wpf/design/simulator.relay.png`

## Spec reference
- `Desktop.Wpf/simulator-flows.md`
  - Flow 9 (Relay Queue Management & Automation)
  - Flow 10 (Fault Injection: Out-of-Order Delivery)
  - **R1 — Transport Interception**

## Goal
Implement the Relay tab as the central place where protocol payloads are queued and delivered.

## Work
UI (Relay page):
- Render one panel per relay-capable peer (e.g., `Vanguard_Actual` in screenshot):
  - queue count
  - per-relay Auto-Deliver toggle
  - “Next” and “All” buttons
  - list of queued payloads with:
    - timestamp
    - type label (e.g., `[MESSAGE]`, `[REVERSE_INVITE]`, `[X3DH_INIT]`, `[X3DH_ACCEPT]`)
    - from → to display (human-friendly)
    - Deliver button

Global controls:
- “Global Auto-Relay All” toggle to enable auto-delivery across all relays.

Fault injection requirements:
- Manual mode must allow:
  - delivering items out of order
  - dropping a queued item
- Nice-to-have (but explicitly called out in flows):
  - reorder items in a queue (move up/down)
  - corrupt payload (bit flip) for MAC failure testing

Target files:
- `Desktop.Wpf/Features/Simulator/SimulatorRelayEmulator.cs`
- `Desktop.Wpf/Features/Simulator/SimulatorStateService.cs`
- `Desktop.Wpf/Features/Simulator/SimulatorOutboundInterceptor.cs`
- `Desktop.Wpf/Features/Simulator/SimulatedPeerRuntimeService.cs`
- `Desktop.Wpf/Features/Simulator/HandshakeSimulatorViewModel.cs`

Implementation note
- The relay tab should be “dumb”: it doesn’t interpret protocol beyond a debug label.
- Delivery should route bytes through the same code paths the app uses (interceptors/ingress), where possible.

## Done when
- Every handshake + message payload surfaces as a relay queue item.
- The user can advance flows by clicking Deliver / Next / All.

---

# Chunk L — Sessions tab: ratchet inspection + group epoch view

## Design references
- (No explicit screenshot provided for sessions; align visually with the other tabs.)

## Spec reference
- `Desktop.Wpf/simulator-flows.md`
  - **R3 — Cryptographic Inspectability**
  - Flow 7/8 messaging ratchet progression
  - Flow 11 group chat (future/concept)

## Goal
Expose cryptographic state in a way that makes message ordering, skipped keys, and ratchet progression obvious.

## Work
UI (Sessions page):
- List active sessions (one per peer), each with:
  - peer name + PKH
  - Root Key hash (or truncated hash)
  - sending chain index
  - receiving chain index
  - skipped keys count

Update behavior:
- When a queued `MESSAGE` is delivered, the session card must reflect ratchet progression immediately.
- If out-of-order delivery occurs, skipped key count changes must be visible.

Group (future/concept):
- Show “GROUP” session cards with:
  - group id
  - participants
  - sender key epoch

Target files (likely):
- `Percolator.Application/Sessions/*` (query surfaces)
- `Desktop.Wpf/Features/Sessions/*` (existing session list patterns)
- `Desktop.Wpf/Features/Simulator/*` (bridge view model)

## Done when
- You can visually confirm ratchet advancement and skipped keys behavior from the UI.

---

# Chunk M — Diagnostics tab: event log, filtering, and export bundle

## Design references
- `Desktop.Wpf/design/simulator.diag.png`

## Spec reference
- `Desktop.Wpf/simulator-flows.md`
  - Information Architecture: Diagnostics

## Goal
Provide a clear, developer-focused diagnostics surface that explains what happened and why.

## Work
UI (Diagnostics page):
- A scrolling chronological log view (like screenshot) with:
  - timestamp
  - short event message
  - optional peer/relay context tags

Events to log (minimum):
- peer created/removed
- peer online/offline
- pre-key publish relationships added/removed
- handshake state transitions
- relay enqueue/deliver/drop/corrupt
- decrypt failures / MAC failures

Filtering (nice-to-have):
- filter by peer
- filter by relay
- filter by event type

Export bundle:
- Add an action (button or overflow menu entry) “Copy Diagnostic Bundle”.
- Output JSON (or write file) containing:
  - peer list (public-only info)
  - relay queue summary
  - recent events
  - session summaries

Target files:
- `Desktop.Wpf/Features/Simulator/*` (central event sink)
- `Desktop.Wpf/Features/Simulator/SimulatorStateService.cs` (emit events)
- `Desktop.Wpf/Features/Simulator/HandshakeSimulatorViewModel.cs` (bind to UI)

## Done when
- You can follow an entire handshake + messaging flow by reading the Diagnostics tab.

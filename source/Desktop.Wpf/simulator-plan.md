# Simulator overhaul (actionable plan)

This document defines an *implementable* plan for overhauling the WPF simulator.

The simulator’s purpose is to:
- Drive **reverse-signal** and **relay** scenarios deterministically.
- Exercise the real Application-layer ingress/commands instead of custom test-only flows.
- Provide a UI to create peers, initiate/accept/reject handshakes, and send messages.

## Simulator transport model (critical)

The simulator is a **network-free driver and observer**.

- The **main node** runs normally and sends messages through the real Application transport abstractions.
- The simulator **does not intercept or route** outbound transport calls.
- The simulator **observes outbound messages** (wiretap) to correlate state transitions and to show what the main node attempted to send.
- When a simulated peer delivers a message *to* the main node, it should call the **real gRPC service entrypoint methods** (or the same Application ingress the gRPC endpoint uses). This keeps the stack “real” except for the physical network.

Implications:
- We do **not** stand up fake gRPC servers per simulated peer.
- We do **not** bypass ingress by calling internal repositories directly.
- Relay behavior is simulated by producing/consuming the **same opaque bytes** that would have flowed through the relay.
- Outbound sends from the main node to a simulated peer are **observed only** (unless we later add a transport plug-in).

## Current constraints / ground truth (must match code)

- Simulator should inject inbound handshakes via:
  - `IEstablishDirectSessionService.QueueInviteAsync(..., isRelayed: false|true, ...)` for reverse-signal invites.
- Relayed opaque intake supports:
  - ratchet ciphertext -> `InternalEnvelope`
  - relayed `EstablishDirectSessionRequest`
  - relayed `InviteHandshakeResponse`

## Glossary

- **Main node**: the currently running desktop app instance.
- **Simulated peer**: a test actor represented in the simulator UI. It does not run its own node process.
- **Reverse-signal invite**: `EstablishDirectSessionRequest` containing signed `InviteHandshakeRequestPayload`.
- **Invite response**: `InviteHandshakeResponse` containing the acceptor’s first ratchet message.

## Explicit non-goals (for now)

- No real sockets / no actual network I/O.
- No real DHT protocol emulation; we simulate “nearest peers” via deterministic lists.
- No real message delivery receipts until chat message plumbing is in place.

---

# Chunk 0 — Align simulator API surface with current handshake ingress

## Goal
Ensure the simulator uses only the current supported ingress points and compiles cleanly.

## Work
- `Desktop.Wpf/Features/Simulator/PendingHandshakeSimulatorService.cs`
  - Provide:
    - `Task<RequestCorrelationId> AddSyntheticPendingAsync(string? displayName, CancellationToken ct)`
    - `Task<IReadOnlyList<RequestCorrelationId>> AddSyntheticPendingsAsync(int count, CancellationToken ct)`
    - `IReadOnlyList<SimulatedPeerSnapshot> SnapshotPeers()`
    - `int CorrelateOutboundSnapshot()`
- `Desktop.Wpf/Features/Simulator/HandshakeSimulatorViewModel.cs`
  - Use returned `RequestCorrelationId` for status display.

## Done when
- Desktop.Wpf builds.
- Desktop.Wpf.Tests builds.

---

# Chunk 1 — Define simulator domain model + persistence contract

## Goal
Persist the simulator state to disk and restore it on startup.

## Data model (serialize as JSON)
Create `Desktop.Wpf/Features/Simulator/SimulatorState.cs` with DTOs:

- `SimulatorStateDto`
  - `int Version`
  - `List<SimulatedPeerDto> Peers`
  - `List<GroupConversationDto> Groups`

- `SimulatedPeerDto`
  - `Guid PeerId` (stable simulator identity, not the crypto peer id)
  - `string? DisplayName`
  - `bool IsOnline`
  - `SimulatedPeerConnectionDto Connection`
  - `List<Guid> KnownPeerIds` (for “nearest peers” simulation)

- `SimulatedPeerConnectionDto`
  - `ConnectionMode Mode` (`Direct`, `ViaRelay`)
  - If `Direct`: `string Host`, `int Port`
  - If `ViaRelay`: `Guid RelayPeerId`

- `GroupConversationDto`
  - `Guid GroupId`
  - `string? Name`
  - `List<Guid> MemberPeerIds` (must include main node + >=2 peers)

## Persistence
Add `ISimulatorStateStore` + `JsonSimulatorStateStore`:
- Save path: `%AppData%/Percolator/simulator-state.json`
- Debounce: 250–500ms after last mutation.

## Tests
- Unit test: save+load roundtrip yields same DTO.
- Unit test: debounce collapses rapid consecutive save requests into one write.

---

# Chunk 2 — Implement peer state machine (UI-facing)

## Goal
Each peer has an explicit state machine that drives available actions.

## State definitions
Create `SimulatedPeerRuntimeState` (not persisted) derived from persisted fields:

- `Ready`
  - can send invite to main node.
- `PendingMainAccept`
  - peer sent invite; awaiting main node approval.
- `PendingSimAccept`
  - main node sent invite; simulated peer must accept/reject.
- `SessionEstablished`
  - can send encrypted app messages (future chunk).
- `Offline`
  - no ingress/egress.

## How transitions occur
- `Ready -> PendingMainAccept`
  - when simulator calls `QueueInviteAsync(...)` successfully.
- `PendingMainAccept -> SessionEstablished`
  - when outbound wiretap observes `InviteHandshakeResponse` with matching `request_correlation_id` and the simulator treats that as “response delivered”.
- `PendingMainAccept -> Ready`
  - if main node rejects/ignores (needs explicit signal; see Questions).

## Tests
- Unit test: new peer starts `Ready`.
- Unit test: calling `AddSyntheticPendingAsync` moves peer to `PendingMainAccept`.
- Unit test: correlating outbound response advances exactly the matching peer.

---

# Chunk 3 — Simulator UI: peer list + add/remove + online toggle

## Goal
Provide a stable UI surface for peer management.

## UI Requirements
- List supports:
  - add peer
  - remove selected
  - multi-select
  - online/offline toggle per peer
  - display `State` + `RequestCorrelationId` (if pending)

## WPF files
- **question**: which view hosts the simulator? (existing window/page)
- ViewModels should use `BindableReactiveProperty` for changing props.
- Styles: use `styles.xaml` base styles.

## Tests
- ViewModel test: adding/removing peers updates collection and triggers save.

---

# Chunk 4 — Handshake actions (main node approval + simulated peer approval)

## Goal
Wire accept/reject to real Application commands.

## Main node approval
- Existing: `ApprovePendingSessionCommand(pendingId)` (already used in `PendingHandshakesMenuViewModel`).
- Simulator needs a way to map `request_correlation_id` -> `PendingSessionId` for the newly queued invite.

### Implementation options
- Option A (recommended): expose a query service in Application layer:
  - `IPendingSessionQueries.TryGetByRequestCorrelationIdAsync(RequestCorrelationId)`
  - returns `PendingSessionId?`
- Option B: in simulator, listen to `PendingSessionCreatedNotification` + fetch pending record and correlate by inspecting its metadata.

**Question**: which do you prefer?

## Simulated peer approval (main node initiating)
This requires the main node to create an invite destined to the simulated peer.

In the current simulator transport model (observer only), the simulator can **observe** the main node attempting to initiate (via outbound wiretap), but it cannot deliver the invite to a simulated peer without adding a transport plug-in.

If we need to support this direction later, we must implement a transport interception layer (a separate chunk) so that outbound `EstablishDirectSessionRequest` messages can be routed into the simulated peer runtime.

---

# Chunk 5 — Relay path coverage in simulator

## Goal
Allow creating peers whose invites/responses are delivered via relay, and demonstrate that the relay is payload-agnostic.

## Mechanics
- Under the **observer + inbound driver** model, the simulator does not intercept the main node’s outbound relay enqueue logic.
- The simulator emulates the relay only at the boundary where the main node receives “opaque bytes fetched from relay”.

- For relayed invite (peer -> main via relay):
  - the simulated peer constructs a real `EstablishDirectSessionRequest` and serializes it.
  - the simulator stores the opaque bytes in an in-memory “relay inbox” keyed by the recipient routing key.
  - when the main node performs a simulated “fetch from relay”, the simulator injects those opaque bytes into the same ingress the real system uses for relay deliveries:
    - `ProcessRelayedOpaquePayloadCommand`

- For relayed invite response (peer -> main via relay):
  - the simulated peer constructs a real `InviteHandshakeResponse` and serializes it.
  - the simulator injects the opaque bytes into `ProcessRelayedOpaquePayloadCommand` on simulated “fetch”.

Notes:
- The simulator is emulating “relay storage + fetch” in memory; it does not need a persistent relay DB.
- The simulator must not attach or persist sender identity when delivering relay blobs.
- Outbound wiretap is used only to observe that the main node attempted relay/direct sends; it is not used to deliver messages.

## Tests
- Unit test: simulated relayed invite drives `QueueInviteAsync(..., isRelayed: true)`.

---

# Chunk 6 — Conversations + group conversation creation (UI only)

## Goal
Model “conversation list” containing both direct peer chats and group chats.

## Requirements
- Conversation list items:
  - Peer conversation: one simulated peer + main node
  - Group conversation: >=2 peers + main node
- Create group button enabled when selection contains >=2 peers in `SessionEstablished`.

## Open question
Do group conversations need to be backed by real `ConversationRepository` now, or is UI-only acceptable until messaging is wired?

---

# Chunk 7 — Message send + delivery simulation (future)

## Goal
Allow sending messages in established sessions and simulate delivery + read receipts.

This chunk depends on having:
- a way for simulator to represent “peer has an established session with main node”, and
- message persistence + envelope routing.

---

# Questions / decisions needed

1) **Mapping correlation -> pending id**: How should the simulator/VM find the `PendingSessionId` corresponding to a `RequestCorrelationId`?
   - Add `IPendingSessionQueries` (recommended), or wire via notifications?

2) **Do we need “main node initiates invite to simulated peer” now?**
   - If yes, we must add a transport plug-in (intercept outbound sends and route to simulated peers).

3) **Group chat backing**: UI-only groups for now, or must create real group conversations in persistence?
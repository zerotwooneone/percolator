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
  - `IEstablishDirectSessionService.QueueInviteAsync(..., isRelayed: false|true, ...)`
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

## Recommended implementation order

The chunks below are written in a conceptual grouping. The recommended implementation order is:

- Chunk A
- Chunk B
- Chunk E
- Chunk C
- Chunk D
- Chunk F

# Chunk A — Simulator shell: peer model, persistence, and UI

## Goal
Provide a persisted peer model and a UI that can add/remove peers and drive actions.

## Work

### A1) Persisted model
Create `Desktop.Wpf/Features/Simulator/SimulatorState.cs`:
- `SimulatorStateDto`
  - `int Version`
  - `List<SimulatedPeerDto> Peers`
  - `List<GroupConversationDto> Groups`
- `SimulatedPeerDto`
  - `Guid PeerId`
  - `string? DisplayName`
  - `bool IsOnline`
  - `SimulatedPeerConnectionDto Connection`
  - `List<Guid> KnownPeerIds`
  - `SimulatedPeerPreKeyStateDto PreKeys`
  - `SimulatedPeerRelayStateDto Relay`
- `SimulatedPeerConnectionDto`
  - `ConnectionMode Mode` (`Direct`, `ViaRelay`)
  - if `Direct`: `string Host`, `int Port`
  - if `ViaRelay`: `Guid RelayPeerId`

- `SimulatedPeerRelayStateDto`
  - `bool IsRelayCapable`
  - `SimulatedRelayOpaqueQueueDto OpaqueQueue`
  - `SimulatedRelayPreKeyStoreDto PreKeyStore`

- `SimulatedRelayOpaqueQueueDto`
  - `int Version`
  - `List<RelayQueuedBlobDto> Items`

- `RelayQueuedBlobDto`
  - `byte[] RecipientRoutingKey`
  - `byte[] OpaqueBytes`
  - `DateTimeOffset EnqueuedUtc`

- `SimulatedRelayPreKeyStoreDto`
  - `int Version`
  - `List<PublishedPreKeyBundleDto> PublishedBundles`

- `PublishedPreKeyBundleDto`
  - `byte[] RecipientPublicKeyHash` (PKH lookup key)
  - `Guid LogicalOwnerPeerId` (who this bundle belongs to; may be a simulated peer or the main node)
  - `byte[] BundleBytes`
  - `DateTimeOffset ExpiresUtc`

- `SimulatedPeerPreKeyStateDto`
  - `byte[]? IdentitySigningKeySpki`
  - `byte[]? SignedPreKeySpki`
  - `byte[]? SignedPreKeySignature`
  - `Guid? SignedPreKeyId`
  - `List<SimulatedOneTimePreKeyDto> OneTimePreKeys`
  - `DateTimeOffset? ExpiresUtc`

- `SimulatedOneTimePreKeyDto`
  - `Guid Id`
  - `byte[] PublicKeySpki`

### A2) Persistence
Add `ISimulatorStateStore` + `JsonSimulatorStateStore`:
- Save path: `%AppData%/Percolator/simulator-state.json`
- Debounce: 250–500ms

### A3) UI
Update simulator UI to support:
- add/remove peers
- online/offline toggle
- display per-peer runtime state

Add Pre-key UI actions per simulated peer:
- Generate pre-key material for this simulated peer (identity signing key + signed pre-key + N one-time keys).
- Publish this simulated peer’s bundles to a selected relay-capable peer’s pre-key store.
- Copy the peer’s lookup key (`PublicKeyHash`) for use in the main node UI.

Add relay-capable peer UI:
- Toggle `IsRelayCapable`.
- View opaque relay queue items.
- View published pre-key bundles (logical owner, PKH, expiry, remaining OTK count).
- Publish a pre-key bundle on behalf of any simulated peer or the main node.

Host window:
- `Desktop.Wpf/Features/Simulator/HandshakeSimulatorWindow.xaml`

## Done when
- You can add/remove peers, toggle online, and state survives restart.
- You can generate a simulated peer’s pre-key material and copy/paste the lookup key into main node workflows.

## Tests
- Save/load roundtrip.
- Debounce collapses rapid updates.

---

# Chunk B — Core handshake runtime state machine + accept/reject actions

## Goal
Make per-peer state transitions explicit and provide accept/reject actions for both main node and simulated peers.

## Work

### B1) Runtime state machine
Create a runtime state machine that represents:
- outbound initiation attempts (pending)
- inbound requests (pending accept/reject)
- established session

At minimum support these UI-facing states:
- `Ready`
- `OutboundPending` (peer initiated something; awaiting remote result)
- `InboundPending` (peer received a handshake request; user must accept/reject)
- `Established`
- `Offline`

### B2) Main node accept/reject requires correlation lookup
Implement `IPendingSessionQueries` in Application layer:
- `IPendingSessionQueries.TryGetByRequestCorrelationIdAsync(RequestCorrelationId)` -> `PendingSessionId?`

Simulator uses this to drive:
- `ApprovePendingSessionCommand(pendingId)`
- (if exists) the corresponding reject command, otherwise add an explicit reject command.

### B3) Simulated peer accept/reject
When the simulated peer receives a handshake request (via transport plug-in or relay fetch injection), the simulator must expose:
- `Accept`
- `Reject`

## Done when
- UI supports accept/reject actions.
- Main node approval path is deterministic via `IPendingSessionQueries`.

---

# Chunk C — Reverse-signal simulation (direct + relayed; both directions)

## Goal
Support reverse-signal end-to-end in four variants:
- peer -> main (direct)
- peer -> main (relayed)
- main -> peer (direct)
- main -> peer (relayed)

## Work

### C1) Peer -> main (direct)
Simulated peer constructs:
- `InviteHandshakeRequestPayload` + signature
- `EstablishDirectSessionRequest`
Deliver via reverse-signal ingress:
- `IEstablishDirectSessionService.QueueInviteAsync(... isRelayed: false ...)`

Main node approves/rejects via Chunk B.

### C2) Peer -> main (relayed)
Simulated peer serializes `EstablishDirectSessionRequest` and enqueues it into simulated relay inbox.
On simulated relay fetch, inject into:
- `ProcessRelayedOpaquePayloadCommand`

### C3) Main -> peer (direct)
Requires transport plug-in from Chunk E to deliver outbound invite to simulated peer runtime.
Simulated peer accept/reject results in `InviteHandshakeResponse` delivered back to main node via the same ingress as gRPC endpoint.

### C4) Main -> peer (relayed)
Main node sends invite destined for a peer via relay.
Transport plug-in observes the outbound attempt and enqueues the opaque blob into simulated relay inbox for the target peer.
Simulated peer fetches, accepts/rejects, and responds (response may also be relayed).

## Done when
- All four reverse-signal variants can be exercised from the simulator UI.

---

# Chunk D — Signal (standard X3DH) simulation (direct + relayed; both directions)

## Goal
Support standard Signal/X3DH initiation (non-reverse) for:
- peer -> main (direct + relayed)
- main -> peer (direct + relayed)

## Work

### D0) Model relay-hosted pre-key stores as the source of pre-key bundles (not peer-to-peer bundle sends)
Standard Signal/X3DH assumes there is a place to publish/fetch pre-key bundles; in the simulator this is modeled as a **relay-capable simulated peer hosting a pre-key store**.

Simulator requirement:
- The initiator (main node or simulated peer) obtains the recipient pre-key bundle by lookup key (PKH) from a selected relay-capable peer.
- The simulator treats the PKH as “out-of-band” input, typically obtained via copy/paste from the simulator UI.

### D1) Add missing standard-handshake ingress (server-side)
Implement the gRPC endpoint that exists in the contract but is not yet implemented:

- File: `Percolator.Application/Network/PercolatorMessageService.cs`
  - Add `override Task<EstablishSessionResponse> EstablishSession(EstablishSessionRequest request, ServerCallContext context)`

Add a dedicated Application-layer ingress/service for standard handshake so `PercolatorMessageService` stays thin:

- New interface (Application): `IStandardHandshakeIngress`
  - `Task<EstablishSessionResponse> HandleAsync(EstablishSessionRequest request, CancellationToken ct)`
  - This will be the sole place that parses/validates `EstablishSessionRequest` and triggers the responder-side handshake.

Responder-side standard handshake bootstrap message:

- Use `HandshakeInitiatorHello` from `internal_messaging.proto` as the initiator’s bootstrap payload.
- Add parsing/handling of `HandshakeInitiatorHello` in the standard handshake ingress.

Note: the existing `IHandshakeService/HandshakeService` is currently a stub and must be upgraded to perform real X3DH/DR bootstrap.

As part of that upgrade, the standard-handshake implementation must consume a pre-key bundle that was fetched by PKH.
The pre-key bundle format to use is the one already present in the codebase:
- `Percolator.Contracts/Protos/internal_messaging.proto` → `GetPreKeyBundleResponse.PreKeyBundle`

### D2) Implement peer -> main standard signal (direct)
Simulated peer (initiator) calls the real gRPC service method:
- `TransportService.EstablishSession(EstablishSessionRequest)`

Main node (responder) processes via `PercolatorMessageService.EstablishSession` -> `IStandardHandshakeIngress` and returns `EstablishSessionResponse`.

### D3) Implement peer -> main standard signal (relayed)
Deliver the initiator bootstrap through the relay as opaque bytes (dumb relay) and inject on simulated “fetch”.

Closed-form constraint from current code:
- `ProcessRelayedOpaquePayloadCommand` currently treats relayed bytes as:
  - `SessionRatchetMessage` (post-session)
  - `EstablishDirectSessionRequest` (reverse-signal invite)
  - `InviteHandshakeResponse` (reverse-signal response)

Therefore, to support **standard signal over relay**, extend `ProcessRelayedOpaquePayloadCommand` to also recognize:
- `HandshakeInitiatorHello`

and route it into the new `IStandardHandshakeIngress` (or a dedicated handler) to complete responder-side bootstrap.

### D4) Implement main -> peer standard signal (direct + relayed)
Main node (initiator) must be able to initiate the standard handshake to a simulated peer.

Precondition (copy/paste workflow):
- Simulator UI exposes the simulated peer’s `PublicKeyHash` (PKH).
- Main node uses that PKH as input to fetch the pre-key bundle from a selected relay-capable peer’s pre-key store.

Direct:
- Transport plug-in (Chunk E) intercepts the outbound `TransportService.EstablishSession` request and delivers it to the simulated peer runtime.
- Simulated peer processes it and returns `EstablishSessionResponse`.

Relayed:
- Transport plug-in (Chunk E) intercepts outbound relay enqueue destined for the simulated peer and puts opaque bytes into the relay emulator.
- Simulated peer fetches, processes `HandshakeInitiatorHello`, and returns the responder’s first ratchet message.

Existing initiator-finalize path to reuse:
- When the initiator later receives the responder’s first ratchet message through relay, it can be finalized using:
  - `HandleHandshakeResponderHelloCommand`
  - `IInitiatorFinalizeService.TryFinalizeFromFirstResponderAsync(...)`

## Done when
- Standard signal initiation can be simulated in both directions, direct and relayed.

---

# Chunk E — Relay emulator + transport plug-in integration

## Goal
Provide a coherent simulator-side relay + outbound interception layer used by Chunks C and D.

## Work

### E1) Relay emulator
Implement an in-memory relay queue:
- key: recipient routing key
- value: FIFO list of opaque byte blobs

Operations:
- `Enqueue(recipientKey, blob)`
- `Fetch(recipientKey, max)`
- `Delete(ackId)` (if modeled)

### E2) Transport plug-in
Implement simulator-only interception of outbound messages sufficient to deliver:
- main -> peer handshake requests (reverse-signal + standard signal)
- main -> peer relayed blobs (enqueue into simulated relay emulator)

## Done when
- Chunks C and D can rely on a single relay emulator + plug-in.

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
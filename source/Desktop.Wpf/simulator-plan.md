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

### C0) Key decisions (Option B)

This chunk is implemented using **Option B**:

- The simulator includes a **full simulated-peer runtime** capable of producing a real `InviteHandshakeResponse` with a valid `InitialRatchetMessage`.
- Simulated peers are **fully persistent**, including **private key material**.
  - **B1.a**: private keys are persisted in `%AppData%/Percolator/simulator-state.json` (Base64 via `byte[]` JSON serialization).
  - This is a development-only feature; no encryption-at-rest is performed in this chunk.

Important code constraints this chunk relies on:

- Reverse-signal invite ingress:
  - gRPC service method:
    - `Percolator.Application/Network/PercolatorMessageService.EstablishDirectSession(EstablishDirectSessionRequest, ServerCallContext)`
    - This method already exists and calls `IEstablishDirectSessionService.QueueInviteAsync(... isRelayed: false ...)`.
  - Application ingress used by relayed injection:
    - `Percolator.Application/Network/IEstablishDirectSessionService.QueueInviteAsync(... isRelayed: true ...)`
- Relayed opaque intake ingress:
  - Relayed messages arrive via the normal gRPC surface and are decrypted/unwrapped by the recipient:
    - `Percolator.Application/Network/PercolatorMessageService.DeliverOpaqueMessage(DeliverOpaqueMessageRequest, ServerCallContext)`
    - `Percolator.Application/Network/DeliverOpaqueMessageHandler` decrypts the session ciphertext into `InternalEnvelope`.
    - If the inner payload is `RelayOpaqueEnvelope`, the handler delegates the inner opaque blob to:
      - `Percolator.Application/Network/Handshake/ProcessRelayedOpaquePayloadCommand`
        - which can parse `EstablishDirectSessionRequest` and `InviteHandshakeResponse`.
- Reverse-signal response ingress:
  - gRPC service method:
    - `Percolator.Application/Network/PercolatorMessageService.DeliverInviteHandshakeResponse(InviteHandshakeResponse, ServerCallContext)`
    - This method already exists and calls `IInviteHandshakeResponseIngress.HandleAsync(InviteHandshakeResponse)`.

### C.B1) Persist simulated peer identity key material (private + public)

Goal: each simulated peer must have stable keys across runs so it can:

- Sign `InviteHandshakeRequestPayload` as inviter.
- Act as acceptor to generate a valid `InviteHandshakeResponse` when it accepts an invite.

Target files:

- `Desktop.Wpf/Features/Simulator/SimulatorState.cs`
- `Desktop.Wpf/Features/Simulator/SimulatorStateService.cs`
- `Desktop.Wpf/Features/Simulator/SimulatedPeerDirectory.cs`
- `Desktop.Wpf/Features/Simulator/SimulatedPeerModel.cs`

Persisted DTO changes (extend `SimulatedPeerDto`):

- `SimulatedPeerReverseSignalKeysDto ReverseSignalKeys`
  - `byte[] IdentitySigningKeyPrivateKeyPkcs8` (required)
  - `byte[] IdentitySigningKeySpki` (required)

Notes:

- Use ECDSA P-256.
- Private key format is **PKCS#8** (from `ECDsa.ExportPkcs8PrivateKey()`).
- SPKI format is from `ECDsa.ExportSubjectPublicKeyInfo()`.
- Migration:
  - If a loaded peer is missing `ReverseSignalKeys`, generate keys and persist on next save.

Done when:

- Existing simulator state loads even if keys are missing (migration/backfill).
- New peers created in UI get keys persisted.
- `SimulatedPeerModel` can expose:
  - `IdentitySigningKeySpki` (public)
  - internal access to `IdentitySigningKeyPrivateKeyPkcs8` (private)

### C.B2) Implement simulated peer runtime: invite receive + accept/reject + response generation

Goal: simulated peer can receive an inbound reverse-signal invite (direct or relayed), and on accept can produce a **real** `InviteHandshakeResponse` with a valid `InitialRatchetMessage`.

Target files:

- `Desktop.Wpf/Features/Simulator/SimulatedPeerModel.cs`
- `Desktop.Wpf/Features/Simulator/SimulatedPeerDirectory.cs`
- New: `Desktop.Wpf/Features/Simulator/SimulatedPeerReverseSignalRuntime.cs` (or similar)

Runtime state added per peer (model-only, not persisted):

- Pending inbound invite:
  - `RequestCorrelationId` (from payload)
  - raw bytes of `EstablishDirectSessionRequest` (for relayed path) or its components
  - source info: direct vs relayed
- Pending outbound invite (peer->main): correlation id (for UI display)

Inbound invite receive APIs (simulator-side):

- `ReceiveDirectInviteFromMainAsync(simPeerId, EstablishDirectSessionRequest request, CancellationToken ct)`
- `ReceiveRelayedInviteFromMainAsync(simPeerId, byte[] opaqueBytes, CancellationToken ct)`
  - called after relay fetch

Accept/reject APIs (simulator-side):

- `AcceptInboundInviteAsync(simPeerId, CancellationToken ct)`
- `RejectInboundInviteAsync(simPeerId, CancellationToken ct)`

Accept behavior:

- Parse/validate `EstablishDirectSessionRequest` + `InviteHandshakeRequestPayload`.
- Use the simulated peer’s **persisted identity signing private key**.
- Produce `InviteHandshakeResponse` where:
  - `RequestCorrelationId` matches.
  - `AcceptorIdentityKey` is simulated peer identity SPKI.
  - `AcceptorX3DhEphemeralKey` and `InitialRatchetMessage` are cryptographically valid.

Response delivery (two paths):

- If invite was **direct**:
  - deliver response to main node via the gRPC service surface:
    - `PercolatorMessageService.DeliverInviteHandshakeResponse(response, serverCallContext)`
    - Use a simulator `ServerCallContext` stub (same pattern as `Percolator.ApplicationTests/Network/PercolatorMessageServiceAdapterTests.cs`).
- If invite was **relayed**:
  - serialize `InviteHandshakeResponse` and enqueue to relay emulator for the main node.
  - on main node “fetch”, simulate relay-host forwarding by calling:
    - `PercolatorMessageService.DeliverOpaqueMessage(...)`
    - with a session-encrypted `InternalEnvelope { RelayOpaqueEnvelope { OpaquePayload = <InviteHandshakeResponse bytes> } }`

Done when:

- A simulated peer can show `InboundPending` after receiving an invite.
- Clicking accept results in `PercolatorMessageService.DeliverInviteHandshakeResponse` (direct) or relay enqueue (relayed).
- Clicking reject clears pending state and updates runtime state.

### C.B3) Relay emulator for reverse-signal (invites + responses)

Goal: implement a simulator relay queue sufficient for reverse-signal in both directions.

Target files:

- New: `Desktop.Wpf/Features/Simulator/SimulatorRelayEmulator.cs`
- `Desktop.Wpf/App.xaml.cs` (DI registration)
- `Desktop.Wpf/Features/Simulator/HandshakeSimulatorViewModel.cs` (UI actions)

Core API:

- `EnqueueToRelayHost(Guid relayHostPeerId, Guid recipientPeerId, byte[] opaqueBytes, string? debugType = null)`
- `FetchFromRelayHost(Guid relayHostPeerId, Guid recipientPeerId, int max) -> IReadOnlyList<SimulatedRelayItem>`

Injection behavior:

- Relayed delivery must be simulated as a **two-hop** flow:
  - **Hop 1 (enqueue): source -> relay host**
    - source encrypts an `InternalEnvelope` containing a `MessageQueueEnvelope.EnqueueOpaqueMessageRequest`.
    - relay host decrypts it via `PercolatorMessageService.DeliverOpaqueMessage(...)` and persists the opaque blob in its queue.
  - **Hop 2 (forward): relay host -> recipient**
    - relay host wraps the queued blob in `InternalEnvelope.RelayOpaqueEnvelope`.
    - relay host encrypts and forwards it to the recipient via `PercolatorMessageService.DeliverOpaqueMessage(...)`.

This matches the real system model described in `session-flow.md`: the relay host only forwards/wraps **opaque encrypted blobs**, and recipients unwrap/decrypt.

Main node fetch (recipient = main node):

- The relay emulator represents **the relay host's queue**.
- The simulator must model:
  - enqueue: (some sender) -> relay host, by delivering a session-encrypted `MessageQueueEnvelope.EnqueueOpaqueMessageRequest` into the relay host via `PercolatorMessageService.DeliverOpaqueMessage(...)`.
  - forward: relay host -> main node, by delivering a session-encrypted `InternalEnvelope { RelayOpaqueEnvelope { OpaquePayload = blob } }` into the main node via `PercolatorMessageService.DeliverOpaqueMessage(...)`.

Notes:

- In the real system, the relay host may forward via an orchestrator loop (see `Percolator.Application/Network/RelayOrchestrator.cs`).
- In the simulator, “fetch” is the user-driven equivalent of triggering that forward step.

Simulated peer fetch (recipient = simulated peer):

- This is symmetric to main node fetch.
- The simulator must model:
  - enqueue: sender -> relay host (via `PercolatorMessageService.DeliverOpaqueMessage(...)` into the relay host)
  - forward: relay host -> simulated peer

If/when the simulator implements a peer-side equivalent of `DeliverOpaqueMessage`, it must use the same `RelayOpaqueEnvelope` unwrap behavior.
Until then, the simulator peer runtime may accept `RelayOpaqueEnvelope.OpaquePayload` bytes directly.

Prerequisites / invariants:

- For the main node to successfully decrypt and unwrap `RelayOpaqueEnvelope`, there must be an established session between:
  - main node ↔ relay host peer
  - This is the session used by `ISecureMessagingService` in `DeliverOpaqueMessageHandler`.

- Bi-directional relay requirement:
  - For a simulated peer to successfully decrypt and unwrap a relayed `RelayOpaqueEnvelope` (if/when the simulator implements a full peer-side `DeliverOpaqueMessage` stack), there must be an established session between:
    - simulated peer ↔ relay host peer
  - Until that full stack exists, the simulator peer runtime may accept `RelayOpaqueEnvelope.OpaquePayload` bytes directly, but the relay emulator must still conceptually model:
    - source -> relay host (enqueue opaque)
    - relay host -> recipient (session-encrypted `DeliverOpaqueMessage`)

Done when:

- Both invites and responses can be enqueued/fetched.
- Fetch triggers the correct delivery path:
  - main side calls `PercolatorMessageService.DeliverOpaqueMessage` and the app unwraps/dispatches internally.
  - peer side calls simulated peer runtime receive method (or a peer-side equivalent of `DeliverOpaqueMessage` if implemented).

### C.B4) Implement main -> peer reverse-signal initiation (simulator-only)

Goal: allow the main node (desktop app identity) to act as the inviter and send a reverse-signal invite to a simulated peer.

This chunk intentionally does **not** require Application-layer support for “send reverse-signal invite”. The simulator constructs the request directly.

Target files:

- `Desktop.Wpf/Features/Simulator/HandshakeSimulatorViewModel.cs`
- `Desktop.Wpf/Features/Simulator/SimulatedPeerItemViewModel.cs`
- New: `Desktop.Wpf/Features/Simulator/MainReverseSignalInviteFactory.cs` (or similar)

Invite construction:

- Build `InviteHandshakeRequestPayload`:
  - `RequestCorrelationId = Guid.NewGuid().ToString()`
  - `InviterHost/InviterPort`:
    - for simulator, use a stable placeholder host/port (e.g. `"simulator"`, `0`) to avoid callback validation issues.
- Sign payload bytes using the **main node identity signing private key** (from active identity context).
- Wrap into `EstablishDirectSessionRequest`.

Delivery:

- Direct: call simulated peer runtime receive method.
- Relayed: enqueue `EstablishDirectSessionRequest.ToByteArray()` into relay emulator for the simulated peer.

Direct-response ingress requirement:

- When the simulated peer accepts a **direct** main->peer invite, it must deliver the resulting `InviteHandshakeResponse` into the main node via:
  - `PercolatorMessageService.DeliverInviteHandshakeResponse(...)`
  - not by calling `IInviteHandshakeResponseIngress` directly.

Done when:

- The simulator can cause a simulated peer to enter `InboundPending` from a main->peer invite.

### C.B5) UI: expose all four reverse-signal variants + accept/reject + relay fetch

Goal: all four reverse-signal variants can be exercised from the simulator UI.

Target files:

- `Desktop.Wpf/Features/Simulator/HandshakeSimulatorWindow.xaml`
- `Desktop.Wpf/Features/Simulator/HandshakeSimulatorViewModel.cs`
- `Desktop.Wpf/Features/Simulator/SimulatedPeerItemViewModel.cs`

UI actions per peer:

- Peer -> Main:
  - `Send Reverse Invite (Direct)`
  - `Send Reverse Invite (Relayed)`
- Main -> Peer:
  - `Send Reverse Invite (Direct)`
  - `Send Reverse Invite (Relayed)`
- When peer is `InboundPending`:
  - `Accept`
  - `Reject`

Additional actions:

- `Fetch relay inbox for main` (deliver via `PercolatorMessageService.DeliverOpaqueMessage` using session-encrypted `RelayOpaqueEnvelope`)
- `Fetch relay inbox for selected peer` (deliver to simulated peer runtime)

Done when:

- You can trigger all 4 variants and see state transitions / correlation IDs in UI.
- Accept/reject is available when inbound pending exists.

### C.B6) Tests / smoke coverage

At minimum add/extend Application-layer tests to validate the relayed forwarding behavior via the normal gRPC ingress:

- `Percolator.ApplicationTests/Network/DeliverOpaqueMessageHandlerTests.cs`
  - Ensure an `InternalEnvelope` with `RelayOpaqueEnvelope` is unwrapped and delegates the inner blob to `ProcessRelayedOpaquePayloadCommand`.
  - Ensure `RelayHostPeerId` is set correctly (remote peer for the direct session).

If additional coverage is needed for reverse-signal payload parsing:

- `Percolator.ApplicationTests/Network/ProcessRelayedOpaquePayloadCommandTests.cs`
  - Ensure `EstablishDirectSessionRequest` bytes and `InviteHandshakeResponse` bytes are recognized.

Done when:

- Relayed forwarding (`DeliverOpaqueMessage` -> decrypt -> `RelayOpaqueEnvelope` -> `ProcessRelayedOpaquePayloadCommand`) is covered by tests.

## Done when
- All four reverse-signal variants can be exercised from the simulator UI.

---

# Chunk C.C — Populate reverse-signal callback host/port (direct + relayed)

## Goal
Ensure all reverse-signal invites contain a valid callback endpoint (host + port) so the acceptor can deliver an `InviteHandshakeResponse` back to the inviter.

This chunk focuses on:
- Adding a single, consistent source of truth for the inviter’s advertised host.
- Using `TransportOptions.GrpcPort` as the inviter’s callback port.
- Updating the existing invite construction code paths to fill these values.

## Scope / message model

The callback endpoint must be embedded in the reverse-signal invite payload:

- `InviteHandshakeRequestPayload`
  - `inviter_host`
  - `inviter_port`

No other messages should be updated to carry callback host/port:
- `InviteHandshakeResponse` remains purely cryptographic + correlation.
- Relay wrappers (e.g., `RelayOpaqueEnvelope`) remain opaque wrappers.

Note: `ReverseSignalCallbackEndpoint` exists in `internal_messaging.proto` as a wrapper message, but this chunk does not require switching the schema to use it. The goal is to reliably populate the effective callback endpoint in the invite payload.

## Config + host lookup abstraction

Port source:
- Use `Percolator.Application.Configuration.TransportOptions.GrpcPort`.

Host source:
- Add an async host lookup abstraction:
  - `IAdvertisedHostLookup` (or similar)
    - `Task<string> GetAdvertisedHostAsync(CancellationToken ct = default)`

Initial implementation (simple; can evolve later):
- Implement `IAdvertisedHostLookup` using configuration only.
- Add a new config value for the advertised host (exact location is flexible):
  - extend `TransportOptions` with `AdvertisedHost` (string)

Behavior:
- If configured host is empty/null, default to `"localhost"`.
- This is intentionally a placeholder strategy; later iterations may compute the correct LAN/WAN IP or NAT-reachable address.

## Code changes (construction sites)

Update all reverse-signal invite creation paths to set:
- `payload.InviterHost = await advertisedHostLookup.GetAdvertisedHostAsync(...)`
- `payload.InviterPort = (uint)transportOptions.GrpcPort` (fallback to 5001 if config is 0, consistent with existing behavior)

Known construction sites:

- Main node inviter (Application layer):
  - `Percolator.Application/Network/MainReverseSignalInviteFactory.cs`
    - Update `InviteHandshakeRequestPayload.InviterHost/InviterPort` to use host lookup + `TransportOptions.GrpcPort`.

- Simulated peer inviter (Desktop simulator):
  - `Desktop.Wpf/Features/Simulator/SimulatedPeerItemViewModel.cs` (`CreatePeerToMainInvite()`)
    - Update `InviteHandshakeRequestPayload.InviterHost/InviterPort`.
    - In simulator mode, the host may be a magic loopback address rather than the advertised host (see transport plug-in chunk). This chunk establishes the mechanism; the simulator may supply a simulator-specific `IAdvertisedHostLookup`.

- Simulator main->peer invite factory (Desktop simulator):
  - `Desktop.Wpf/Features/Simulator/ReverseSignalInviteFactory.cs` (if used for main->peer invite creation)
    - Update `InviteHandshakeRequestPayload.InviterHost/InviterPort`.

- Any simulator-only handshake generation utilities:
  - `Desktop.Wpf/Features/Simulator/PendingHandshakeSimulatorService.cs` (if it constructs `InviteHandshakeRequestPayload`)
    - Update `InviteHandshakeRequestPayload.InviterHost/InviterPort`.

## Validation / done when

- All code paths that construct `InviteHandshakeRequestPayload` populate `InviterHost` and `InviterPort` via the new abstraction IAdvertisedHostLookup and `TransportOptions.GrpcPort`.
- Reverse-signal invite validation in `Percolator.Application/Network/EstablishDirectSessionService.cs` continues to succeed without special-casing.
- The host lookup implementation can later be swapped (LAN IP selection, NAT traversal, magic loopback range) without changing the handshake model.

---

# Chunk C.D — Simulator port + loopback addressing for main -> simulated peer outbound routing

## Goal
Enable the main window to send outbound transport messages to simulated peers reliably, without running additional network servers.

This chunk introduces a simulator-specific destination endpoint model so the main node can target simulated peers and the outbound gRPC clients can short-circuit delivery into the in-process simulator runtime.

## Motivation
The main node’s outbound transport stack targets peers by `DnsEndPoint` (host + port). In simulator mode, if we use `localhost:<GrpcPort>` for simulated peers, the main node will send messages to itself.

We need a deterministic way to represent “this endpoint is a simulated peer” using only host/port, without expanding endpoint types or adding schemes.

## Config
Extend `Percolator.Application.Configuration.TransportOptions`:
- `GrpcPort` (existing): the main node’s gRPC server port.
- `SimulatorPort` (new): a simulator-only port used as a routing key for simulator-bound outbound messages.

Notes:
- `SimulatorPort` does not need to be bound to a real socket.
- It exists to disambiguate simulator destinations from real localhost endpoints.

## Addressing model (magic loopback range)
Reserve a loopback-only IPv4 range to represent simulated peers:
- `127.77.0.0/16`

Allocation:
- Each simulated peer is assigned a stable IP literal within this range.
- Persist the assigned host in simulator state:
  - `SimulatedPeerConnectionDto.Host` stores the assigned IP string.
- Persist the assigned port in simulator state:
  - `SimulatedPeerConnectionDto.Port` stores `TransportOptions.SimulatorPort`.

## Outbound interception (transport plug-in)
Implement simulator-only interception for outbound gRPC sends.

Interception rule:
- If destination `host` parses to an IP in `127.77.0.0/16` and destination `port == SimulatorPort`, route in-process.
- Otherwise, use the normal gRPC networking path.

Required outbound calls to intercept for Chunk C:
- Reverse-signal callback delivery:
  - `TransportService.DeliverInviteHandshakeResponse(InviteHandshakeResponse)`
- Ongoing secure messaging:
  - `TransportService.DeliverOpaqueMessage(DeliverOpaqueMessageRequest)`

## Invite creation impact
When the simulated peer is the inviter (peer -> main reverse-signal), its `InviteHandshakeRequestPayload.InviterHost/InviterPort` must be set to its simulator endpoint:
- `InviterHost = <assigned 127.77.x.y>`
- `InviterPort = SimulatorPort`

## Done when
- The main node can send outbound `DeliverInviteHandshakeResponse` and `DeliverOpaqueMessage` to simulated peers using the simulator endpoints (127.77/16 + `SimulatorPort`).
- No simulator-bound outbound messages are sent to `localhost:<GrpcPort>`.
- Non-simulator endpoints continue to use normal gRPC networking unchanged.

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
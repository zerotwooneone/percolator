
Red–Green–Refactor strategy
- Red: write failing unit tests and focus on creating the best possible interfaces. The build must succeed, but we expect new tests to fail at this point.
- Green: implement the minimal changes to pass tests (contracts, minimal lookup, handler change).
- Refactor: clean up the code, check to make sure business logic is sound, anticipate edge cases, and naming without changing behavior; tests remain passing.

- Two-step session inference (fast/slow path) for ratchet key misses
  - Rationale: In rare cases (e.g., a DH ratchet step that advances the remote header key before our index updates), the fast indexed lookup by header key may miss. We must still be able to correctly identify the session without any peer-supplied identifiers.
  - Fast Path (common case):
    - Lookup (`SelfIdentityId`, `RatchetPublicKey`) → `DirectSessionId` using `RatchetKeyIndex`.
    - Expected to succeed for ~99% of messages.
  - Slow Path (new key case): if fast path misses:
    - Iterate over all active sessions for the current `SelfIdentityId`.
    - For each session S:
      - Take the incoming header’s ratchet public key K_remote.
      - Perform trial DH using S’s current local DH ratchet private key.
      - Derive a trial message key using the ratchet KDFs.
      - Attempt to decrypt/authenticate the ciphertext.
      - If authentication succeeds, we’ve identified the correct session S. Update S’s state to the new remote header key, finalize decryption, and proceed.
    - After success, upsert `RatchetKeyIndex(SelfIdentityId, K_remote) → S.DirectSessionId`.

- Confirm no trust is placed on client-provided identifiers; only cryptographic headers drive session inference.
- Ensure logs do not persist sender identity for relayed messages; logs must be free of identifying content (see 16.1.1).

Non-functional contract (Signal-like):
- End-to-end encrypted blobs only; queue stores opaque bytes, never plaintext or keys.
- No permanent storage; messages are deleted immediately after ack
- FIFO per destination device; preserve enqueue order on delivery.
- Best-effort immediate delivery if recipient is online; otherwise enqueue.

### 1 Command plan to drive Step 17 via MediatR (user-facing GUI/CLI)
- **Identity/peer setup (existing)**
  - `CreateSelfIdentityCommand(name, passphrase?)` (exists) — create self identities for Host, Alice, Bob, Charlie.
  - `SetPeerNameByPublicKeyCommand(name, spki)` (exists) — mutual naming by SPKI on each node for host and peers.

- **DHT (existing, may extend)**
  - `DhtProbeCommand(endpoint, targetName, SelfIdentityName?)` (exists) — use for “findNode”. It returns discovered peers (PKHs). If needed, extend the response shape for richer UI feedback.
  - New: `DhtFindNodeCommand(hostEndpoint)` — user-facing alias over `DhtProbeCommand` that normalizes discovered PKHs for UI consumption.
  - New: `DhtPingCommand(targetNameOrEndpoint)` — user-facing ping that triggers the online presence check; this should implicitly cause the internal envelope queue to be pumped by the receiver when they are online.

- **Prekeys (existing)**
  - `SubmitPreKeysCommand(targetPeerName, OneTimeKeyCount, ExpiresUtc)` (exists) — Alice/Bob publish prekey bundles to Host.
  - `Percolator.Prekey.GetPreKeyBundleQuery(peerPkh)` (exists) — Host-side query used by request flow.
  - `RequestPreKeyBundleByPkhCommand(hostEndpoint, targetPkh)` (exists) — client command to retrieve target’s prekey bundle from Host.

- **Message queue relay (internal, not user-facing)**
  - Internals like `EnqueueOpaqueMessageCommand`, `FetchQueuedMessagesQuery`, `TryRelayNextForPeerCommand` remain internal implementation details and are not exposed as user commands.

- **Handshake (existing building blocks, propose new orchestration)**
  - Building blocks (exist):
    - `ComposeAndEnqueueInitiatorHelloCommand(hostEndpoint, targetPkh)` — compose initiator hello and enqueue via Host (exists).
    - `HandleHandshakeInitiatorHelloCommand(payload)` (exists) — receiver path.
    - `HandleHandshakeResponderHelloCommand(payload)` (exists) — initiator path after responder reply.
    - `ProcessRelayedOpaquePayloadCommand(payload)` (exists) — generic opaque inbound processing.
  - New orchestration (to add):
    - `InitiateHandshakeViaHostCommand(hostEndpoint, targetPkh)` — one-shot command on Bob/Charlie that:
      1) Requests Alice/Bob prekey bundle from Host.
      2) Composes InitiatorHello using that bundle.
      3) Enqueues opaque InitiatorHello to Host MQ for delivery to target PKH.

- **Direct session mapping (existing)**
  - `EstablishDirectSessionCommand` (exists) — used by gRPC path; for MQ-based path, DR sessions are established implicitly by handshake handlers and stored via session store.

- **Group operations (existing app-level commands)**
  - `CreateGroupFromIdentityKeysCommand(selfIdentityId, groupGuid, participantSpkis[], name, creatorSpki)` (exists) — Alice creates group with Bob+Charlie.
  - `GrantGroupAdminAppCommand(selfIdentityId, groupGuid, granteeSpki)` (exists) — Alice grants Bob admin.
  - `UpdateGroupMembershipAppCommand(selfIdentityId, groupGuid, membersToAdd[], membersToRemove[], leaveGroup?)` (exists) — Bob removes/rehydrates Charlie.
  - `UpdateGroupInfoAppCommand(selfIdentityId, groupGuid, newName)` (exists) — optional.

- **Group key distribution/adoption (existing)**
  - Distribution flows through MQ inside admin handlers; recipients process via `ReceiveKeyDistributionCommand` (exists) which calls `GroupKeyOperations.ImportGroupKeyAsync`.
  - Optional test signal: subscribe to `KeyVersionAdoptedNotification` (exists) to await adoption per node.

- **New user-facing helpers (to add)**
  - `DhtFindNodeCommand(hostEndpoint)` — friendly “find node” UX wrapping `DhtProbeCommand`.
  - `DhtPingCommand(targetNameOrEndpoint)` — user-visible ping that also serves as a natural trigger for online checks.

These commands allow a new integration test to: (1) set names; (2) DHT probe; (3) publish prekeys; (4) drive opaque handshakes via Host MQ using a single orchestration command per initiator; (5) create and mutate groups; (6) assert key adoption and participant sets — all without direct gRPC calls, only MediatR application commands.

### 17 Correct Phase 2 test order (fix Phase2_Prekeys_Dht_And_Sessions_Establish)
    1) Alice↔Host connect; mutual naming by SPKI; Alice probes DHT (0 nodes); Alice publishes prekeys.
    2) Bob↔Host connect; mutual naming by SPKI; Bob probes DHT and discovers Alice’s PKH.
    3) Bob enqueues opaque handshake initiator envelope to Alice’s PKH; assert Alice responds and Bob completes the session establishment over opaque messages.
    4) Bob publishes his prekey bundle to Host.
    5) Charlie↔Host connect; mutual naming by SPKI.
    6) Charlie probes DHT and discovers both Alice and Bob (verify both PKHs are visible).
    7) Charlie initiates opaque handshakes to Alice and Bob via MQ (Host relays with `RelayOpaqueEnvelope`), both complete; verify `DirectSession` rows exist Charlie↔Alice and Charlie↔Bob.
    9) Alice creates a new group chat with members Bob and Charlie; assert repository state (conversation created, participants = {Alice, Bob, Charlie}).
    10) Alice grants Bob admin rights for that group; assert admin set contains Alice and Bob.
    11) Bob removes Charlie from the group; assert participants = {Alice, Bob} and Charlie no longer has access to future group messages.
    12) Bob sends a group message; assert only Alice receives/sees it (Charlie must NOT receive it).
    13) Bob re-adds Charlie to the group; assert participants = {Alice, Bob, Charlie} and key distribution state updated.
    14) Alice sends a group message; assert both Bob and Charlie receive it.

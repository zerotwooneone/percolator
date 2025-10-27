
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
  - `DhtProbeCommand(endpoint, targetName, SelfIdentityName?)` (exists) — use directly for “findNode”. It returns discovered peers (PKHs). If needed, extend the response shape for richer UI feedback.
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
  - `DhtPingCommand(targetNameOrEndpoint)` — user-visible ping that also serves as a natural trigger for online checks.
    - Test guidance: when Client A enqueues messages for Client B on the host, have Client B call `DhtPingCommand("host")` to trigger the host to pump B's pending enqueued messages.

These commands allow a new integration test to: (1) set names; (2) DHT probe; (3) publish prekeys; (4) drive opaque handshakes via Host MQ using a single orchestration command per initiator; (5) create and mutate groups; (6) assert key adoption and participant sets — all without direct gRPC calls, only MediatR application commands.

### 2 Application-layer sender utility and routing corrections
- **Problem**: Client handlers (e.g., `CreateGroupFromIdentityKeysHandler`) must not call host-side commands like `EnqueueOpaqueMessageCommand` directly. That command is part of the host’s store-and-forward queue. Clients should either send directly over an established Direct Session or ask the host to enqueue by sending a protobuf `MessageQueueEnvelope.enqueue_opaque_message_request` within an `InternalEnvelope`.

- **Solution**: Add a small application-layer sender utility used by commands in Step 1 to deliver per-recipient messages.
  - Name: `IRemoteEnvelopeSender` (app-layer interface) with implementation `RemoteEnvelopeSender`.
  - Responsibilities per recipient:
    - Build the inner `InternalEnvelope` (e.g., `ChatEnvelope.create_group`, `SignedAdminOperation`, etc.).
    - Try direct delivery when a Direct Session exists (encrypt with session and send via `IMessageTransportService`).
    - If no session, build `InternalEnvelope.message_queue_envelope.enqueue_opaque_message_request` pointing at the host, carrying the ciphertext and recipient PKH (SHA-256 of SPKI) for store-and-forward.
    - Exclude self and infrastructure peers from recipients.
    - Idempotency: rely on message-specific keys (e.g., `group_conversation_guid`, `op_id`, or `message_id`) to avoid duplicates on receiver.
  - Collaborators:
    - `IDirectSessionRepository` / `IDirectSessionManager` (to detect session and encrypt per recipient).
    - `IPeerPublicSigningKeyStore` (resolve PKH from SPKI when needed).
    - `IMessageTransportService` (send DR ciphertext to peer or host).
  - Transport mapping:
    - Direct: send DR-ciphertext of `InternalEnvelope` to recipient.
    - Host enqueue: send `InternalEnvelope` with `MessageQueueEnvelope.enqueue_opaque_message_request` to host; host persists and later relays using `RelayOpaqueEnvelope`.
  - Test guidance: after Client A enqueues for Client B on host, have Client B call `DhtPingCommand("host")` to flush delivery.

- **Implemented**
  - Introduced a single-recipient API: `IRemoteEnvelopeSender.SendChatEnvelopeToPeerAsync(ChatEnvelope, RecipientRoute)`.
  - `RemoteEnvelopeSender` now:
    - Sends via Direct Session when available.
    - Falls back to host by sending `MessageQueueEnvelope.enqueue_opaque_message_request` inside an `InternalEnvelope` to the peer named `host`, when recipient PKH is provided.
  - `CreateGroupFromIdentityKeysHandler` now dispatches per-recipient using the sender and passes each recipient’s PKH (derived from SPKI) to enable host enqueue fallback.
  - `AdminOperationDispatcher` now delegates per-recipient dispatch to `IRemoteEnvelopeSender`.
    - To support host enqueue for admin ops, extended `IPeerPublicSigningKeyStore` with `GetPublicKeyHashByPeerIdAsync(PeerId)` and used it to supply PKH per recipient.
  - Phase 17 integration test updated to call `DhtPingCommand("host")` on recipients after Alice creates the group to flush queued deliveries.

- **Immediate refactor**
  - Update command handlers to use the sender utility instead of referencing queue commands directly. For `CreateGroupFromIdentityKeysCommand`, dispatch `ChatEnvelope.create_group` via the utility with per-recipient routing. Admin operations already use an application dispatcher that performs enqueue-and-relay; it can delegate to the same utility for consistency.

### 3 Revisit Step 1 commands: remote-peer notifications (protobuf wrappers/messages)
- **Envelope rules (default for app messages)**
  - Build `InternalEnvelope` with `chat_envelope` for chat/admin payloads.
  - Encrypt per recipient using the active DirectSession to produce opaque ciphertext.
  - When relaying via host, wrap ciphertext in `RelayOpaqueEnvelope.opaque_payload`.
  - Timestamps are UTC (`google.protobuf.Timestamp` from `DateTimeOffset.UtcNow`). GUIDs are serialized as 16 bytes.

- **Routing rules**
  - Prefer direct-session delivery. If no session exists, enqueue via host using recipient PKH (SHA-256 of SPKI) where supported by the command.
  - Exclude self and infrastructure peers from recipient lists (implementation detail in app layer).
  - Test guidance: delivery observation pattern — after enqueuing messages for other clients, those other clients should call `DhtPingCommand("host")` to prompt the host to deliver their pending queue.

- **Command-by-command mapping**
  - CreateGroupFromIdentityKeysCommand
    - Notify all initial members (excluding self/infra):
      - `InternalEnvelope.chat_envelope.create_group` (`CreateGroup`)
        - `group_conversation_guid`, `initial_participant_identity_keys`, `name`, `creator_identity_key`.
      - Idempotency key: `group_conversation_guid` per recipient.

  - GrantGroupAdminAppCommand
    - Control-plane broadcast to all current members:
      - `InternalEnvelope.chat_envelope.signed_admin_operation` (`SignedAdminOperation`)
        - `payload.group_conversation_guid`, `payload.op_id`, `payload.sent_timestamp_utc`, `payload.admin_sequence_number`.
        - `payload.operation.grant_admin.grantee_public_key`.
        - `signature` over `payload.ToByteArray()` by admin key.
    - Per-recipient follow-up (acting admin to each member):
      - `InternalEnvelope.chat_envelope.key_distribution` (`KeyDistributionPayload`).
    - Finalization broadcast after confirmations:
      - `InternalEnvelope.chat_envelope.admin_commit_operation` (`SignedAdminCommitOperation`).

  - UpdateGroupMembershipAppCommand
    - Control-plane broadcast to all current members:
      - `InternalEnvelope.chat_envelope.signed_admin_operation.payload.operation.update_group_membership`
        - `members_to_add` (participant GUID bytes), `members_to_remove` (GUID bytes), `leave_group`.

  - UpdateGroupInfoAppCommand
    - Control-plane broadcast to all current members:
      - `InternalEnvelope.chat_envelope.signed_admin_operation.payload.operation.update_group_info`
        - `new_group_name`, `new_group_avatar`.

  - InitiateHandshakeViaHostCommand (orchestrates opaque handshake)
    - Outbound from initiator to target (via host relay):
      - Standalone `HandshakeInitiatorHello` (NOT wrapped in `InternalEnvelope`).
      - Carried inside `RelayOpaqueEnvelope.opaque_payload` over the relay transport.
    - Responder’s inner reply (preferred form) after session established:
      - Application payloads resume using `InternalEnvelope` with appropriate inner messages.

  - DhtPingCommand (presence/health; not a state change notification)
    - `InternalEnvelope.dht_envelope.ping_request` / `ping_response`.

  - Prekey commands (server-bound; not peer notifications)
    - `SubmitPreKeyBundleRequest` / `GetPreKeyBundleRequest` via `PrekeyEnvelope` to host; no direct peer notification.

- **Idempotency and ordering**
  - Group creation: dedupe on `(group_conversation_guid)` per recipient.
  - Admin ops: dedupe on `payload.op_id`; enforce per-recipient send order: operation → key distribution → commit.
  - Text messages (when used): dedupe on `TextMessage.message_id`.

### 17 Correct Phase 2 test order (fix Phase2_Prekeys_Dht_And_Sessions_Establish)
    1) Alice↔Host connect; mutual naming by SPKI; Alice probes DHT (0 nodes); Alice publishes prekeys.
    2) Bob↔Host connect; mutual naming by SPKI; Bob probes DHT and discovers Alice’s PKH.
    3) Bob enqueues opaque handshake initiator envelope to Alice’s PKH; assert Alice responds and Bob completes the session establishment over opaque messages.
    4) Bob publishes his prekey bundle to Host.
    5) Charlie↔Host connect; mutual naming by SPKI.
    6) Charlie probes DHT and discovers both Alice and Bob (verify both PKHs are visible).
    7) Charlie initiates opaque handshakes to Alice and Bob via MQ (Host relays with `RelayOpaqueEnvelope`), both complete; verify `DirectSession` rows exist Charlie↔Alice and Charlie↔Bob.
    9) Alice creates a new group chat with members Bob and Charlie; assert repository state (conversation created, participants = {Alice, Bob, Charlie}).
      9a) Alice sends a message, assert Bob and Charlie receive it.
    10) Alice grants Bob admin rights for that group; assert admin set contains Alice and Bob.
    11) Bob removes Charlie from the group; assert participants = {Alice, Bob} and Charlie no longer has access to future group messages.
    12) Bob sends a group message; assert only Alice receives/sees it (Charlie must NOT receive it).
    13) Bob re-adds Charlie to the group; assert participants = {Alice, Bob, Charlie} and key distribution state updated.
    14) Alice sends a group message; assert both Bob and Charlie receive it.

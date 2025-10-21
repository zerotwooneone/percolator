
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


### 1 Unit tests for new application handlers (Red)
    - Project: `Percolator.ApplicationTests`
    - Tests for `CreateGroupConversationHandler`:
      - Seeds exactly one creator admin key in `IGroupAdminKeyStore`.
      - Calls `IConversationRepository.CreateGroupAsync(...)` with all participants.
      - Sends a `CreateGroup` envelope to all other participants (exclude self) through `IMessageTransportService`.
    - Tests for admin op handlers:
      - `GrantGroupAdminCommand` signs canonical payload, applies locally (admin key added), and broadcasts to all other participants, excluding originator.
      - `RevokeGroupAdminCommand` removes admin key and broadcasts (exclude self).
      - `UpdateGroupMembershipCommand` updates participants via repository and broadcasts (exclude self); emits `GroupMembershipChangedNotification`.
      - `UpdateGroupInfoCommand` updates metadata and broadcasts (exclude self).
    - Use fakes/mocks for:
      - `IMessageTransportService`, `IConversationRepository`, `IGroupAdminKeyStore`, `ISelfParticipantIdProvider`, and key material provider.
    - Ensure idempotency: repeated application of the same op id should no-op via `IGroupAdminOpStore.TryAddAsync`.

### 2 Application handler: CreateGroupConversation (Green)
    - Define `CreateGroupConversationCommand` in `Percolator.Application.Apps.Chat` with:
      - `SelfIdentityId`, `GroupConversationGuid`, `ParticipantIdentityKeysSpki[]`, `Name?`.
    - Implement `CreateGroupConversationHandler` responsibilities:
      1) Resolve and persist initial participants via `IConversationRepository.CreateGroupAsync(...)`.
      2) Seed creator admin key in `IGroupAdminKeyStore.AddKeyAsync(...)` using the creator's identity SPKI.
      3) Build `Contracts.CreateGroup` envelope including `creator_identity_key` and broadcast to all other participants (exclude self) via `IMessageTransportService` after ensuring sessions (`ConnectToPeerCommand`) where necessary.
      4) Update local state idempotently (if the local node also receives the envelope later, handling remains safe).
      5) Publish domain/application notifications as needed (e.g., `GroupMembershipChangedNotification`).
    - Acceptance criteria:
      - Local admin key table for the creator is updated.
      - All other participants receive the CreateGroup envelope through normal transport.
      - Error handling: fail fast on malformed inputs (null/empty SPKIs, empty participant list, invalid GUIDs) and log PKH hashes (not raw keys) for diagnostics.
      - Consistency in timestamps: capture a single `DateTimeOffset.UtcNow` snapshot per command and reuse it for any timestamps needed by this handler.
    - Notes:
      - Canonicalization: do not extend canonicalization logic; protobuf serialization is sufficient. Reuse existing helpers only where already present.

### 3 Application handlers for group admin operations (Green)
    - Define App-layer commands that the UI/CLI will use:
      - `GrantGroupAdminCommand(SelfIdentityId, GroupGuid, GranteeSpki)`
      - `RevokeGroupAdminCommand(SelfIdentityId, GroupGuid, GranteeSpki)`
      - `UpdateGroupMembershipCommand(SelfIdentityId, GroupGuid, MembersToAdd?, MembersToRemove?, LeaveGroup?)`
      - `UpdateGroupInfoCommand(SelfIdentityId, GroupGuid, NewGroupName?)`
    - Implement handlers to:
      1) Construct an `AdminOperationPayload`, compute canonical bytes, sign with the local identity key.
      2) Apply locally by sending `ApplySignedAdminOperationCommand` (verifies signature against historical keys and updates repositories/admin key store).
      3) Broadcast the resulting `SignedAdminOperation` to all other participants (exclude self) via `IMessageTransportService`.
      4) For membership-changing ops, publish `GroupMembershipChangedNotification` to trigger key distribution flows.
    - Acceptance criteria:
      - Local apply succeeds only if the incoming admin sequence number is strictly greater than the last applied for that group (monotonic admin seq).
      - Error handling: fail fast on malformed inputs (missing grantee key, invalid group GUID, empty signature).
      - Consistency in timestamps: capture a single `DateTimeOffset.UtcNow` snapshot per command and reuse it for payload timestamps.
    - Notes:
      - Canonicalization: do not extend canonicalization logic; protobuf serialization is sufficient. Reuse existing helpers only where already present.
      - Keep protobuf contracts in `Percolator.Contracts` only; handlers and orchestration stay in `Percolator.Application` per architecture rules.

### 4 Integrate new App commands into Phase 17 (Refactor)
    - Update `Percolator.ApplicationIntegrationTests/ChatMessaging/GroupChatEndToEndTests.cs`:
      - Replace direct uses of `SendChatEnvelopeAsync` for local state updates with App-layer commands:
        - Use `CreateGroupConversationCommand` instead of building/sending `CreateGroup` locally.
        - Use `GrantGroupAdminCommand`, `UpdateGroupMembershipCommand`, `RevokeGroupAdminCommand`, `UpdateGroupInfoCommand` instead of crafting `SignedAdminOperation` directly.
      - Keep transport path intact for inter-node distribution (handlers broadcast to other participants; exclude self).
      - Remove ad-hoc mirroring and any duplicate admin key seeding in test scaffolding.
    - Acceptance criteria:
      - Test remains green while code is simplified and aligned with real-world App-layer usage.

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


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

### 1 Handle relayed plaintext HandshakeInitiatorHello
We need to add a fallback handler to ProcessRelayedOpaquePayloadHandler- if the peer fails to decrypt the payload as ratchet message then we should attempt to parse it as a HandshakeInitiatorHello

### 2 Relayed message wrapper
The host should include a host-specific message id (guid) to RelayOpaqueEnvelope (source\Percolator.Contracts\Protos\internal_messaging.proto) when sending relayed messages to peers.
This will require adding a new AckId column to MessageQueueItemDbo because we do not want to expose our internal queue id to peers.
A peer, upon receiving a relay message should attempt to respond with a ratchet message for the host whose payload is a new RelayOpaqueResponse which contains the Message Ack Id.
The host, when receiving a RelayOpaqueResponse, should attempt to delete the Acknowledged message.
This will require a new DB migration. There is no need handle existing databases, a new sqlite file will be created. Message Ack Id is required and not null.
Messages should be attempted to be sent directly to the peer, and if that fails then the message should be queued.
RelayOpaqueResponse will not be a part of InternalEnvelope - instead this will be the payload in a ratchet message to the host. The response will be returned as a return from the grpc method delivering the message as a payload inside the ratchet message.
The ack is purely the synchronous return of the DeliverOpaqueMessage RPC.
If relay message delivery fails, we should stop sending queued messages until the next signal the peer is online.
Replay/double-ack handling: deleting by AckId should be idempotent; unknown acks are logged and ignored.

#### 2.1 Relay for offline peers
We need to replace the existing catch-up behavior with one in which the host begins to relay messages queued (one by one) for a peer as soon as the peer comes online. This allows the peer to acknowledge each message as a return value to the proto method. 
The host will then handle the RelayOpaqueResponse and delete the corresponding message.
We should no longer need FetchAndDeleteAsync after this change, remove this method.
A peer coming online can be generalized when an internal envelope is handled from the peer. From the host perspective, we should handle the internal envelope as a signal that the peer is online.

### 3 Chat

#### 3.2 Message Queue
Add code path for TextMessage, ReadReceipt, EmojiAnnotation, DeliveredReceipt, SignedAdminOperation, and SignedAdminCommitOperation to be delivered over message queue
Messages should be queued immediately and then attempted to be delivered directly - if direct delivery is acked then delete the queued message after the ack.

### 17 Correct Phase 2 test order (fix Phase2_Prekeys_Dht_And_Sessions_Establish)
    1) Alice↔Host connect; mutual naming by SPKI; Alice probes DHT (0 nodes); Alice publishes prekeys.
    2) Bob↔Host connect; mutual naming by SPKI; Bob probes DHT and discovers Alice’s PKH.
    3) Bob enqueues opaque handshake initiator envelope to Alice’s PKH; assert Alice responds and Bob completes the session establishment over opaque messages.
    4) Bob publishes his prekey bundle to Host.
    5) Charlie↔Host connect; mutual naming by SPKI.
    6) Charlie probes DHT and discovers both Alice and Bob (verify both PKHs are visible).
    7) Charlie initiates opaque handshakes to Alice and Bob via MQ (Host relays with `RelayOpaqueEnvelope`), both complete; verify `DirectSession` rows exist Charlie↔Alice and Charlie↔Bob.
    8) Ensure mutual naming is completed among all peers where needed (e.g., after successful handshakes).
    9) Alice creates a new group chat with members Bob and Charlie; assert repository state (conversation created, participants = {Alice, Bob, Charlie}).
    10) Alice grants Bob admin rights for that group; assert admin set contains Alice and Bob.
    11) Bob removes Charlie from the group; assert participants = {Alice, Bob} and Charlie no longer has access to future group messages.
    12) Bob sends a group message; assert only Alice receives/sees it (Charlie must NOT receive it).
    13) Bob re-adds Charlie to the group; assert participants = {Alice, Bob, Charlie} and key distribution state updated.
    14) Alice sends a group message; assert both Bob and Charlie receive it.
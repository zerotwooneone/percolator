# Percolator.Apps.Chat Implementation Plan

## Summary & Architectural Constraints
- **Target Project**: `Percolator.Apps.Chat` (Messaging plugin for 1:1 direct channels and zero-knowledge/epoch-managed multi-party group channels).
- **Architectural Rules (Rule 1 & Rule 2)**:
  - Depends **only** on `Percolator.Domain` and `Percolator.PluginSdk`.
  - Zero reference to infrastructure/transport/storage libraries (no gRPC, SQLite, EF Core, or socket APIs).
  - Strict serialization boundary: Application layer handles pure C# DTOs and delegates serialization to `IPayloadSerializer`. Concrete Protobuf contracts (`.proto`) and Google Protobuf code live strictly in `Percolator.Infrastructure2.Serialization`.
  - Implements `IAppPlugin` (`AppId.Chat = 0x01`) and `IAppPayloadHandler` from `Percolator.PluginSdk`.
  - Content isolation: Chat handles user conversations and messages. File manifests, file transfer negotiations, and discovery pings are handled by other apps and never appear in chat feeds.
  - Test-first implementation: All behaviors must have corresponding unit tests in `Percolator.Apps.Chat.Tests` using in-memory test doubles.

---

## Milestone 1: Chat Plugin Architecture & Payload Handling

### 1.1 Plugin Definition & Binary DTOs
- **`ChatPlugin`**: Implements `IAppPlugin` with `AppId = 0x01` and semantic versioning.
- **Application Payload DTOs** (Protobuf serialization abstracted via `IPayloadSerializer`):
  - `TextMessageDto`: Text content, timestamp, quote/reply context.
  - `ReactionDto`: Emoji reaction reference, target payload ID, remove flag.
  - `ReceiptDto`: Delivered/Read status marker, target payload ID.
- **`ChatPayloadHandler`**:
  - Implements `IAppPayloadHandler` for `AppId.Chat`.
  - Deserializes inbound payloads via `IPayloadSerializer`.
  - Maps and delivers messages to domain channel aggregates:
    - Direct: `DirectChannel.AppendPayload`.
    - Group: `GroupChannel.AppendPayload`.

### 1.2 Test Doubles & Unit Tests (`Percolator.Apps.Chat.Tests/Ingress`)
- `ChatPayloadHandlerTests.HandleInboundAsync_TextMessage_AppendsPayloadToChannel`: asserts domain payload appended.
- `ChatPayloadHandlerTests.HandleInboundAsync_EmojiReaction_AppliesReaction`: asserts reaction state delta.
- `ChatPayloadHandlerTests.HandleInboundAsync_ReadReceipt_UpdatesReadMarker`: asserts read receipt marker advance.
- `ChatPayloadHandlerTests.HandleInboundAsync_CorruptedPayload_ReturnsDeserializationError`: asserts rejection on malformed bytes.

---

## Milestone 2: Group Chat Sender Key Distribution & Out-of-Order Buffering

### 2.1 Group Session Bootstrapping
- **`SenderKeyDistributionPayload`**: Structured application payload (`ChannelId`, `ChainKey`, `Iteration`, `Epoch`) distributed across 1:1 pairwise sessions to bootstrap group chat sender-key ratchets.
- **`IUnknownGroupMessageCache` Port**:
  - Bounded FIFO cache (capacity 100 messages per channel) holding out-of-order group messages received before the author's `SenderKeyDistributionPayload` has arrived.
- **Group Message Decryption & Cache Flusher**:
  - Upon receiving an out-of-order group message where receiver session is missing: buffers into `IUnknownGroupMessageCache`.
  - Upon receiving `SenderKeyDistributionPayload`: initializes `GroupReceiverSession` and immediately flushes/decrypts buffered messages in order.

### 2.2 Test Doubles & Unit Tests (`Percolator.Apps.Chat.Tests/SenderKeys`)
- **`InMemoryUnknownGroupMessageCache`**: Test double implementing `IUnknownGroupMessageCache`.
- `ChatPayloadHandlerTests.HandleInboundAsync_SenderKeyDistribution_InitializesGroupReceiverSession`: asserts sender key ratchet setup.
- `ChatPayloadHandlerTests.HandleInboundAsync_SenderKeyDistribution_FlushesUnknownMessageCache`: asserts buffered messages decrypted upon key arrival.
- `UnknownMessageCacheTests.Enqueue_WhenLimitExceeded_EvictsOldestMessage`: asserts bounded FIFO invariant.

---

## Milestone 3: Group Membership Invitations & Metadata Encryption

### 3.1 Group Invitation Consent State Machine
- **`PendingGroupInvitation` Entity**:
  - Models inbound invitations to join group channels (`InvitationId`, `ChannelId`, `InviterId`, `InitialMembers`, `ReceivedAtUtc`, `Status`: `Pending`, `Accepted`, `Declined`, `Expired`).
  - Ensures local user consent before joining a group channel, generating presentation proofs, or deriving group sender keys.
- **`IGroupInvitationService` & `GroupInvitationService`**:
  - Emits events when an invitation is received.
  - On user acceptance: joins `GroupChannel`, records repository state, and begins sender-key distribution.
  - On user decline: records state and rejects further channel payloads.

### 3.2 Test Doubles & Unit Tests (`Percolator.Apps.Chat.Tests/Invitations`)
- `GroupInvitationServiceTests.ReceiveInvitation_EmitsPendingInvitation`: asserts invitation state created.
- `GroupInvitationServiceTests.AcceptInvitation_JoinsGroupAndInitiatesKeyExchange`: asserts group joining flow.
- `GroupInvitationServiceTests.DeclineInvitation_MarksDeclinedAndSuppressesFutureTraffic`: asserts rejection flow.

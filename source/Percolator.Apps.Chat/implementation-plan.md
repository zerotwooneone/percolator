# Percolator.Apps.Chat Implementation Plan

## Summary & Architectural Constraints
- **Target Project**: `Percolator.Apps.Chat` (Messaging plugin for 1:1 direct channels and zero-knowledge/epoch-managed multi-party group channels).
- **Architectural Rules (Rule 1 & Rule 2)**:
  - Depends **only** on `Percolator.Domain` and `Percolator.PluginSdk`.
  - Zero reference to infrastructure/transport/storage libraries (no gRPC, SQLite, EF Core, or socket APIs).
  - Strict serialization boundary: Application layer handles pure C# DTOs and delegates serialization to `IPayloadSerializer`. Concrete Protobuf contracts (`.proto`) and Google Protobuf code live strictly in `Percolator.Infrastructure2.Serialization`.
  - Implements `IAppPlugin` (`AppId.Chat = 0x01`) and `IAppPayloadHandler` from `Percolator.PluginSdk`.
  - Content isolation: Chat handles user conversations and messages. File manifests, file transfer negotiations, and discovery pings are handled by other apps and never appear in chat feeds.
  - **Single Source of Truth & CQRS Separation (Domain Writes vs. Fast-Path Queries)**:
    - **Write Path (Mutations & State Transitions)**: To eliminate the risk of split-brain state, divergent message sequences, and out-of-sync histories, `Percolator.Domain.Channels` (`DirectChannel` and `GroupChannel`) is the **sole authoritative owner** of channel membership, administrative roles, cryptographic epochs, and the chronological payload timeline. Inbound messages are committed through domain aggregates and persisted via `IChannelRepository`.
    - **Read Path (Fast-Path Queries Skipping Domain Aggregates)**: The UI and read models do **NOT** hydrate heavy domain channel aggregates (which contain full member lists, child collections, and cryptographic state). Instead, read operations use dedicated read-only query ports (`IChatMessageQueryService` and `IConversationListQueryService`) to query indexed database tables directly, retrieving only the required paged timeline fields with sub-millisecond latency.
  - **Cryptographic Logging Guardrails**:
    - The Chat application must strictly honor cryptographic logging guardrails (`CryptographyOptions.EnableCryptographicMaterialLogging = false` by default).
    - Diagnostic, audit, and trace logging must **never** record sensitive cryptographic key material (chain keys, message keys, sender keys, or key derivation hashes) or user plaintexts. Only sanitized operational metadata (e.g. channel IDs, timestamps, payload size) may appear in logs.
  - Test-first implementation: All behaviors must have corresponding unit tests in `Percolator.Apps.Chat.Tests` using in-memory test doubles.

---

## Milestone 1: Chat Plugin Architecture, Payload Handling & CQRS Queries

### 1.1 Plugin Definition & Binary DTOs
- **`ChatPlugin`**: Implements `IAppPlugin` with `AppId = 0x01` and semantic versioning.
- **Application Payload DTOs** (Protobuf serialization abstracted via `IPayloadSerializer`):
  - `TextMessageDto`: Text content, timestamp, quote/reply context.
  - `ReactionDto`: Emoji reaction reference, target payload ID, remove flag.
  - `ReceiptDto`: Delivered/Read status marker, target payload ID.
- **`ChatPayloadHandler` (Write Path)**:
  - Implements `IAppPayloadHandler` for `AppId.Chat`.
  - Deserializes inbound payloads via `IPayloadSerializer`.
  - Delivers and commits message payloads directly into domain channel aggregates:
    - Direct: `DirectChannel.AppendPayload` via `IChannelRepository`.
    - Group: `GroupChannel.AppendPayload` via `IChannelRepository`.

### 1.2 Fast-Path Read Query Ports (Bypassing Domain Aggregates)
- **`IChatMessageQueryService`** (`Percolator.Apps.Chat.Ports`):
  - Read-only query port for paginated conversation messages and reactions.
  - `Task<IReadOnlyList<ChatMessageReadModel>> GetPagedMessagesAsync(ChannelId channelId, int beforeSequence, int limit, CancellationToken ct = default);`
  - `Task<ChatMessageReadModel?> GetMessageByIdAsync(PayloadId payloadId, CancellationToken ct = default);`
  - Returns lightweight `ChatMessageReadModel` projections directly from indexed persistence without hydrating aggregate roots.
- **`IConversationListQueryService`** (`Percolator.Apps.Chat.Ports`):
  - Read-only query port for the user's conversation list feed.
  - `Task<IReadOnlyList<ConversationSummaryReadModel>> GetRecentConversationsAsync(PublicIdentityId ownerId, CancellationToken ct = default);`
  - Returns channel ID, display title, last message snippet, last activity timestamp, and unread counts directly from database indices.

### 1.3 Test Doubles & Unit Tests (`Percolator.Apps.Chat.Tests/Ingress`)
- `ChatPayloadHandlerTests.HandleInboundAsync_TextMessage_AppendsPayloadToDomainChannel`: asserts payload committed directly to domain channel.
- `ChatPayloadHandlerTests.HandleInboundAsync_EmojiReaction_AppliesReactionToTimeline`: asserts reaction projection delta.
- `ChatPayloadHandlerTests.HandleInboundAsync_ReadReceipt_UpdatesReadMarker`: asserts read receipt projection advance.
- `ChatPayloadHandlerTests.HandleInboundAsync_CorruptedPayload_ReturnsDeserializationError`: asserts rejection on malformed bytes.
- `InMemoryChatMessageQueryServiceTests.GetPagedMessagesAsync_ReturnsRequestedPage_WithoutDomainHydration`: asserts fast query performance and correct paging.

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
  - On user acceptance: joins `GroupChannel` (mutating the domain aggregate as single source of truth), records repository state, and begins sender-key distribution.
  - On user decline: records state and rejects further channel payloads.
- **`IGroupInvitationQueryService`** (`Percolator.Apps.Chat.Ports`):
  - Read-only query port for UI invitation notifications:
    - `Task<IReadOnlyList<GroupInvitationSummaryReadModel>> GetPendingInvitationsAsync(PublicIdentityId recipientId, CancellationToken ct = default);`

### 3.2 Test Doubles & Unit Tests (`Percolator.Apps.Chat.Tests/Invitations`)
- `GroupInvitationServiceTests.ReceiveInvitation_EmitsPendingInvitation`: asserts invitation state created.
- `GroupInvitationServiceTests.AcceptInvitation_JoinsDomainGroupAndInitiatesKeyExchange`: asserts domain group aggregate mutation.
- `GroupInvitationServiceTests.DeclineInvitation_MarksDeclinedAndSuppressesFutureTraffic`: asserts rejection flow.
- `GroupInvitationQueryServiceTests.GetPendingInvitationsAsync_ReturnsOnlyPendingInvitations`: asserts query accuracy.

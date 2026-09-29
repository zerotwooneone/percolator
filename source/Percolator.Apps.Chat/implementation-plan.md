# Percolator.Apps.Chat Implementation Plan

## Summary & Architectural Constraints
- **Target Project**: `Percolator.Apps.Chat` (Messaging plugin for 1:1 direct conversations and zero-knowledge/epoch-managed group conversations).
- **Architectural Rules (Rule 1 & Rule 2)**:
  - Depends **only** on `Percolator.Domain` and `Percolator.PluginSdk`.
  - Zero reference to infrastructure/transport/storage libraries (no gRPC, SQLite, EF Core, or socket APIs).
  - Implements `IAppPlugin` (`AppId.Chat = 0x01`) and `IAppPayloadHandler` from `Percolator.PluginSdk`.
  - Test-first implementation: All behaviors must have corresponding unit tests in `Percolator.Apps.Chat.Tests` using in-memory test doubles.

---

## Milestone 1: Chat Plugin Architecture & Payload Handling

### 1.1 Plugin Definition & Binary DTOs
- **`ChatPlugin`**: Implements `IAppPlugin` with `AppId = 0x01` and semantic versioning.
- **Application Payload DTOs**:
  - `TextMessageDto`: Text content, timestamp, quote/reply context.
  - `ReactionDto`: Emoji reaction reference, target message ID, remove flag.
  - `ReceiptDto`: Delivered/Read status marker, target message ID.
- **`ChatPayloadHandler`**:
  - Implements `IAppPayloadHandler` for `AppId.Chat`.
  - Deserializes inbound payloads via `IPayloadSerializer`.
  - Maps and delivers messages to domain aggregates:
    - Direct: `DirectConversation.AppendMessage`.
    - Group: `GroupConversation.AppendMessage`.

### 1.2 Test Doubles & Unit Tests (`Percolator.Apps.Chat.Tests/Ingress`)
- `ChatPayloadHandlerTests.HandleInboundAsync_TextMessage_AppendsMessageToConversation`: asserts domain message appended.
- `ChatPayloadHandlerTests.HandleInboundAsync_EmojiReaction_AppliesReaction`: asserts reaction state delta.
- `ChatPayloadHandlerTests.HandleInboundAsync_ReadReceipt_UpdatesLastReadMessageId`: asserts read receipt marker advance.
- `ChatPayloadHandlerTests.HandleInboundAsync_CorruptedPayload_ReturnsDeserializationError`: asserts rejection on malformed bytes.

---

## Milestone 2: Group Chat Sender Key Distribution & Out-of-Order Buffering

### 2.1 Group Session Bootstrapping
- **`SenderKeyDistributionPayload`**: Structured application payload (`ConversationId`, `ChainKey`, `Iteration`, `Epoch`) distributed across 1:1 pairwise sessions to bootstrap group chat sender-key ratchets.
- **`IUnknownGroupMessageCache` Port**:
  - Bounded FIFO cache (capacity 100 messages per conversation) holding out-of-order group messages received before the author's `SenderKeyDistributionPayload` has arrived.
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
  - Models inbound invitations to join group conversations (`InvitationId`, `ConversationId`, `InviterId`, `InitialMembers`, `ReceivedAtUtc`, `Status`: `Pending`, `Accepted`, `Declined`, `Expired`).
  - Ensures local user consent before joining a group, generating presentation proofs, or deriving group sender keys.
- **`IGroupInvitationService` & `GroupInvitationService`**:
  - Intercepts inbound group invitations.
  - `AcceptInvitationAsync`: Creates local `GroupConversation` aggregate, registers sender keys, and emits acceptance event.
  - `DeclineInvitationAsync`: Purges invitation and ignores subsequent group traffic.

### 3.2 Group Profile Metadata Encryption
- **Encrypted Group Profile**:
  - Symmetric key derivation from `GroupMasterKey` to encrypt/decrypt group title, avatar bytes, and bio.
  - Keeps group visual identity encrypted from relays while synchronizing across legitimate members.

### 3.3 Test Doubles & Unit Tests (`Percolator.Apps.Chat.Tests/Invitations`)
- `PendingGroupInvitationTests.AcceptInvitation_InitializesGroupConversation_AndTransitionsStatus`: asserts consent workflow.
- `PendingGroupInvitationTests.DeclineInvitation_TransitionsStatusToDeclined`: asserts clean decline.
- `GroupMetadataEncryptionTests.EncryptAndDecrypt_RoundTripsGroupTitleAndAvatar`: verifies AES-GCM metadata packaging.

---

## Milestone 4: Privacy-Preserving Link Previews

### 4.1 Link Preview Architecture
- **`ILinkPreviewFetcher` Port**: Abstraction for fetching raw HTML, keeping HTTP I/O strictly in infrastructure.
- **`LinkPreviewParser`**:
  - Extracts OpenGraph title, description, and thumbnail image bytes.
  - Caps thumbnail size (< 32KB) and scrubs tracking query parameters before transmission.

### 4.2 Unit Tests (`Percolator.Apps.Chat.Tests/LinkPreviews`)
- `LinkPreviewParserTests.ParsePreview_ValidHtml_GeneratesThumbnailUnder32KB`: asserts compact privacy preview generation.
- `LinkPreviewParserTests.ParsePreview_WithTrackingParameters_ScrubsUtmTags`: asserts privacy preservation.

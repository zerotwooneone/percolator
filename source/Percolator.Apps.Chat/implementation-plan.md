# Percolator.Apps.Chat Implementation Plan

## 1. Summary & Architectural Constraints
- **Target Project**: `Percolator.Apps.Chat` (Messaging plugin for 1:1 direct channels and zero-knowledge/epoch-managed multi-party group channels).
- **Architectural Rules (Rule 1 & Rule 2)**:
  - Depends **only** on `Percolator.Domain` and `Percolator.PluginSdk`.
  - Zero reference to infrastructure/transport/storage libraries (no gRPC, SQLite, EF Core, or socket APIs).
  - Strict serialization boundary: Application layer handles pure C# DTOs and delegates serialization to `IPayloadSerializer`. Concrete Protobuf contracts (`.proto`) and Google Protobuf code live strictly in `Percolator.Infrastructure2.Serialization`.
  - Implements `IAppPlugin` (`AppId.Chat = 0x01`) and `IAppPayloadHandler` from `Percolator.PluginSdk`.
  - Content isolation: Chat handles user conversations, rich text formatting, media attachments, reactions, and group metadata. File manifests, BitTorrent swarms, and discovery pings are handled by other apps and never appear in chat feeds.
  - **Single Source of Truth & CQRS Separation (Domain Writes vs. Fast-Path Queries)**:
    - **Write Path (Mutations & State Transitions)**: To eliminate the risk of split-brain state, divergent message sequences, and out-of-sync histories, `Percolator.Domain.Channels` (`DirectChannel` and `GroupChannel`) is the **sole authoritative owner** of channel membership, administrative roles, cryptographic epochs, and the chronological payload timeline. Inbound messages are committed through domain aggregates and persisted via `IChannelRepository`.
    - **Read Path (Fast-Path Queries Skipping Domain Aggregates)**: The UI and read models do **NOT** hydrate heavy domain channel aggregates (which contain full member lists, child collections, and cryptographic state). Instead, read operations use dedicated read-only query ports (`IChatMessageQueryService` and `IConversationListQueryService`) to query indexed database tables directly, retrieving only the required paged timeline fields with sub-millisecond latency.
  - **Cryptographic Separation of Concerns**:
    - Sender key ratchet generation and distribution across 1:1 pairwise channels are owned by `Percolator.Application` (`GroupKeyDistributionService`) via `AppId.SystemControl` (0x00). `Percolator.Apps.Chat` operates strictly as an application payload consumer on top of established channels.
  - **Cryptographic Logging Guardrails**:
    - The Chat application must strictly honor cryptographic logging guardrails (`CryptographyOptions.EnableCryptographicMaterialLogging = false` by default).
    - Diagnostic, audit, and trace logging must **never** record sensitive cryptographic key material (chain keys, message keys, symmetric media keys, or key derivation hashes) or user plaintexts. Only sanitized operational metadata (e.g. channel IDs, timestamps, payload size) may appear in logs.
  - Test-first implementation: All behaviors must have corresponding unit tests in `Percolator.Apps.Chat.Tests` using in-memory test doubles.

---

## 2. Milestone 1: Chat Plugin Architecture, Payload DTOs & CQRS Queries

### 2.1 Plugin Definition & Binary DTOs
- **`ChatPlugin`**: Implements `IAppPlugin` with `AppId = 0x01` and semantic versioning.
- **Application Payload DTOs** (Protobuf serialization abstracted via `IPayloadSerializer`):
  - `ChatMessageDto`:
    - `PayloadId Id`: Unique message identifier.
    - `ChannelId ChannelId`: Target direct or group channel.
    - `PublicIdentityId AuthorId`: Sender identity.
    - `DateTimeOffset TimestampUtc`: Message dispatch timestamp.
    - `string Content`: Rich text content (Markdown format).
    - `IReadOnlyList<MentionDto> Mentions`: Byte-offset mentions (`IdentityId`, `StartOffset`, `Length`).
    - `ReplyContextDto? ReplyContext`: Reply reference (`ParentPayloadId`, `ParentAuthorId`, `Snippet`).
    - `LinkPreviewDto? LinkPreview`: Sender-generated client-side link preview.
    - `IReadOnlyList<MediaAttachmentDto> Attachments`: Inline or out-of-band media descriptors.
    - `int? ExpireAfterSeconds`: Disappearing message timer (null = permanent).
    - `DateTimeOffset? EditedAtUtc`: Last edit timestamp (null = original).
  - `MessageEditDto`:
    - `PayloadId TargetPayloadId`: Message being edited.
    - `string NewContent`: Updated Markdown text.
    - `IReadOnlyList<MentionDto> NewMentions`: Updated mentions.
    - `DateTimeOffset EditedAtUtc`: Edit timestamp.
  - `MessageTombstoneDto`:
    - `PayloadId TargetPayloadId`: Message being deleted.
    - `PublicIdentityId RequestedBy`: Identity requesting deletion.
    - `DateTimeOffset DeletedAtUtc`: Deletion timestamp.
  - `ReactionUpdateDto`:
    - `PayloadId TargetPayloadId`: Target message identifier.
    - `string Emoji`: Unicode emoji representation.
    - `ReactionAction Action`: `Add` or `Remove`.
    - `DateTimeOffset TimestampUtc`: Action timestamp.
  - `ReadReceiptDto`:
    - `ChannelId ChannelId`: Target channel.
    - `PayloadId UpToPayloadId`: Highest read message sequence marker.
    - `DateTimeOffset ReadAtUtc`: Read timestamp.
  - `SystemNoticeDto`:
    - `ChannelId ChannelId`: Target channel.
    - `SystemNoticeType Type`: `SafetyNumberChanged`, `MemberJoined`, `MemberLeft`, `RolePromoted`, `EpochAdvanced`.
    - `PublicIdentityId AffectedIdentity`: Identity involved.
    - `DateTimeOffset TimestampUtc`: Notice timestamp.

### 2.2 Rich Text Formatting & Mention Offset Invariants
- **Markdown Formatting**:
  - The UI parser supports bold (`**`), italic (`*`), strikethrough (`~~`), inline code (`` ` ``), blockquotes (`>`), and syntax-highlighted code blocks (` ``` `).
- **Byte-Span Mentions**:
  - Mentions are represented as `MentionDto(PublicIdentityId IdentityId, int StartOffset, int Length)` measured in UTF-8 byte spans.
  - This eliminates display name spoofing and brittle raw-text regex parsing, matching the Signal Protocol's secure mention model.

### 2.3 `ChatPayloadHandler` (Write Path)
- Implements `IAppPayloadHandler` for `AppId.Chat`.
- Deserializes inbound payloads via `IPayloadSerializer`.
- Validates message constraints (e.g. edit window within 24 hours, author matches original sender, tombstone authorization).
- Commits payloads directly into domain channel aggregates:
  - Direct: `DirectChannel.AppendPayload` via `IChannelRepository`.
  - Group: `GroupChannel.AppendPayload` via `IChannelRepository`.
- Dispatches domain events for reaction, read receipt, edit, and tombstone updates.

### 2.4 Fast-Path Read Query Ports (Bypassing Domain Aggregates)
- **`IChatMessageQueryService`** (`Percolator.Apps.Chat.Ports`):
  - Read-only query port for paginated conversation messages, reactions, and attachments.
  - `Task<IReadOnlyList<ChatMessageReadModel>> GetPagedMessagesAsync(ChannelId channelId, int beforeSequence, int limit, CancellationToken ct = default);`
  - `Task<ChatMessageReadModel?> GetMessageByIdAsync(PayloadId payloadId, CancellationToken ct = default);`
  - Returns lightweight `ChatMessageReadModel` projections (including aggregated reactions and attachments) directly from indexed database storage.
- **`IConversationListQueryService`** (`Percolator.Apps.Chat.Ports`):
  - Read-only query port for the user's conversation list feed.
  - `Task<IReadOnlyList<ConversationSummaryReadModel>> GetRecentConversationsAsync(PublicIdentityId ownerId, CancellationToken ct = default);`
  - Returns channel ID, display title, last message snippet, last activity timestamp, and unread counts directly from database indices.

---

## 3. Milestone 2: Media Attachments, Out-of-Band Blob Storage & Privacy Previews

### 3.1 Media Attachment Strategy (Inline vs. Out-of-Band)
To satisfy the strict 64 KB Double Ratchet payload limit without resorting to heavy peer swarms:
1. **Inline Media ($\le$ 32 KB)**:
   - Short voice clips, animated stickers, custom emojis, and low-resolution image previews / BlurHashes.
   - Encrypted directly within the E2EE channel payload via `MediaAttachmentDto.InlineBytes`.
   - Zero additional network requests required; available instantaneously on message arrival.
2. **Out-of-Band Encrypted Blobs (32 KB to 50 MB)**:
   - Photos, audio tracks, GIF animations, and short video clips.
   - **EXIF Geolocation Scrubbing**: All GPS coordinates, camera serial numbers, and device tags are stripped client-side before padding and encryption.
   - **Anti-Traffic Padding**: Plaintext media is padded to discrete size buckets before encryption.
   - **Chunked STREAM AEAD**: Encrypted in $64\text{ KB}$ chunks with sequential counter nonces and independent auth tags via `Percolator.PluginSdk.IBlobStorageService`.
   - **Audio Waveforms & Layout Stability**:
     - Voice notes embed normalized amplitude bytes (`Waveform`) inline in `BlobReference` for immediate waveform rendering without audio decoding.
     - Images and videos mandate `Width` and `Height` in `BlobReference` to eliminate Cumulative Layout Shift (CLS) in chat feeds.
   - `MediaAttachmentDto` carries the lightweight `BlobReference` containing the symmetric key, base nonce, ciphertext SHA-256 digest, unpadded size, and storage mode.

### 3.2 Channel Topologies & Media Expectations
Media behavior is strictly driven by the channel's configured topology:
1. **1:1 Direct Channel (No Relay)**:
   - **Expectation**: Media can **only** be transferred when **both peers are online concurrently**.
   - **Staged Pull Workflow**: Sender calls `UploadBlobAsync`, which encrypts and stages chunks in the sender's local disk store. The tiny `BlobReference` is sent via chat message. When the recipient's client receives the message, it pulls chunks directly from the sender's node over a dedicated peer stream.
   - If the remote peer is offline, the Chat UI disables the upload button or queues the message with status: *"Waiting for recipient to come online to stream media"*.
   - Consumes zero relay bandwidth or disk storage.
2. **1:1 Relayed Channel**:
   - **Expectation**: The channel's designated relay acts as the single media storage endpoint.
   - Sender uploads ciphertext out-of-band to the channel relay.
   - Recipient downloads ciphertext asynchronously from that same relay upon coming online.
3. **Group Channel**:
   - **Expectation**: The group's single common relay acts as the authoritative media store for all group members (pure Signal model).
   - Sender uploads the encrypted, padded blob **exactly once** to the group relay.
   - Sender distributes the `BlobReference` inside their group message using their **Sender Key** ratchet.
   - All group members stream chunks from that same common relay (via manual tap-to-download or Wi-Fi auto-download).
4. **Relay Quota & Policy Enforcement**:
   - If the channel relay rejects an upload because its storage is full or disabled, `IBlobStorageService` returns an explicit error (`RelayStorageQuotaExceeded` or `RelayBlobStorageDisabled`), and the Chat UI displays a clear explanation to the user.
5. **Decoupled Performance Pipeline**:
   - Blob uploads/downloads run **out-of-band BEFORE `OutboundEgressPipeline`**, ensuring the ratchet lock (`IChannelLockService`) is never held during file uploads.
   - Multi-megabyte media bypasses the SQLite Outbox completely, preserving the featherweight transaction outbox invariant.

### 3.3 Privacy-Preserving Link Previews
- **Zero Recipient Deanonymization (Signal-Aligned Privacy)**:
  - The recipient client must **never** automatically scrape URLs or fetch OpenGraph metadata from the internet. Doing so leaks the recipient's IP address and browsing behavior to external web servers.
- **Sender-Generated Preview Model**:
  - The sender's client optionally generates OpenGraph metadata (`Title`, `Description`, `CanonicalUrl`) and a small encrypted thumbnail blob.
  - Previews are embedded in `LinkPreviewDto` within the E2EE ratchet envelope.
  - Recipient renders the preview purely from the received E2EE data without external HTTP calls.

---

## 4. Milestone 3: Message Lifecycles, Reactions & Group Governance

### 4.1 Message Edits & Deletions (Tombstones)
- **Message Edits**:
  - Allowed within a 24-hour window from original send timestamp.
  - Must be signed by the original author.
  - Inbound edits update the local message read model, marking the message with an `(edited)` indicator and updating `EditedAtUtc`.
- **Message Deletions / Remote Retraction**:
  - Authors may delete a message within 24 hours of posting.
  - Group administrators may delete messages from any member if permitted by channel policy.
  - Rendered as a tombstone placeholder: *"This message was deleted"*, preserving thread sequence integrity and reply linkages while permanently purging the ciphertext and local media cache.

### 4.2 Disappearing Messages
- Configured at channel level or overridden per message via `ExpireAfterSeconds`.
- **Sender Timer**: Starts counting down immediately upon successful message dispatch.
- **Recipient Timer**: Starts counting down upon message read receipt or first display in the active viewport.
- **Local Eviction Worker**: Automatically prunes expired messages and invokes `IBlobStorageService.DeleteLocalCacheAsync` to purge associated media blobs from disk.

### 4.3 Emoji Reactions & Read Aggregation
- **Reaction Model**:
  - `ReactionUpdateDto` dispatches `Add` or `Remove` actions for a Unicode emoji.
  - Idempotent: repeated adds from the same user do not duplicate reactions.
- **Pre-Aggregated Read Projection**:
  - `ReactionGroupReadModel` exposed on `ChatMessageReadModel`:
    - `string Emoji`: The emoji glyph.
    - `int Count`: Total reactions.
    - `bool ReactedByMe`: Whether the local user reacted.
    - `IReadOnlyList<PublicIdentityId> ReactorIds`: Identity list for tooltip/details modal.

### 4.4 Group Invitation Consent State Machine
- **`PendingGroupInvitation` Entity**:
  - Inbound invitations to join group channels require explicit local user consent before joining or exchanging keys.
  - States: `Pending`, `Accepted`, `Declined`, `Expired`.
- **`IGroupInvitationService` & `GroupInvitationService`**:
  - Emits events when an invitation is received.
  - On user acceptance: joins `GroupChannel` (mutating the domain aggregate as single source of truth) and informs `GroupKeyDistributionService`.
  - On user decline: records state and rejects further channel traffic.
- **`IGroupInvitationQueryService`** (`Percolator.Apps.Chat.Ports`):
  - Read-only query port for UI invitation notifications:
    - `Task<IReadOnlyList<GroupInvitationSummaryReadModel>> GetPendingInvitationsAsync(PublicIdentityId recipientId, CancellationToken ct = default);`

### 4.5 In-Stream System Notices & Read Status
- **System Notices (`SystemNoticeDto`)**:
  - Inserted directly into the chat timeline for safety number rotations, member additions/removals, admin promotions, and epoch advancements.
- **Read Cursors & Unread Badges**:
  - `ReadReceiptDto` advances the remote read marker.
  - `IConversationListQueryService` computes unread badge counts based on local read cursor vs. latest channel sequence numbers.

---

## 5. Milestone 4: Test Suite Specification

### 5.1 Ingress & Payload Tests (`Percolator.Apps.Chat.Tests/Ingress`)
- `ChatPayloadHandlerTests.HandleInboundAsync_TextMessage_AppendsPayloadToDomainChannel`: Verifies write path aggregate mutation.
- `ChatPayloadHandlerTests.HandleInboundAsync_WithMentions_PreservesByteOffsets`: Verifies byte offset span validation.
- `ChatPayloadHandlerTests.HandleInboundAsync_EmojiReaction_AppliesReactionToTimeline`: Verifies reaction aggregation delta.
- `ChatPayloadHandlerTests.HandleInboundAsync_MessageEdit_WithinWindow_UpdatesMessage`: Verifies 24-hour edit window acceptance.
- `ChatPayloadHandlerTests.HandleInboundAsync_MessageEdit_PastWindow_RejectsEdit`: Verifies rejection of edits after 24 hours.
- `ChatPayloadHandlerTests.HandleInboundAsync_MessageEdit_NonAuthor_RejectsEdit`: Verifies non-author edit rejection.
- `ChatPayloadHandlerTests.HandleInboundAsync_MessageTombstone_ReplacesWithDeletedPlaceholder`: Verifies tombstone mutation and media cache eviction trigger.
- `ChatPayloadHandlerTests.HandleInboundAsync_CorruptedPayload_ReturnsDeserializationError`: Verifies robust error handling on corrupted data.

### 5.2 Media & Blob Storage Tests (`Percolator.Apps.Chat.Tests/Media`)
- `ChatMediaServiceTests.SendMediaAsync_InlineMedia_EmbedsBytesDirectly`: Verifies $\le$ 32 KB payload remains inline.
- `ChatMediaServiceTests.SendMediaAsync_1to1Direct_WhenPeerOnline_StagesAndSendsReference`: Verifies local staging and peer pull in 1:1 direct.
- `ChatMediaServiceTests.SendMediaAsync_1to1Direct_WhenPeerOffline_ReturnsPeerOfflineError`: Verifies failure expectation when peer is offline in non-relayed 1:1.
- `ChatMediaServiceTests.SendMediaAsync_RelayedChannel_UploadsToChannelRelay`: Verifies upload to designated channel relay.
- `ChatMediaServiceTests.SendMediaAsync_GroupChannel_UploadsOnceToCommonRelay`: Verifies single upload to group common relay.
- `ChatMediaServiceTests.SendMediaAsync_RelayQuotaExceeded_ReturnsQuotaError`: Verifies clear error propagation on relay rejection.
- `ChatMediaServiceTests.ReceiveMediaAsync_OutofBandMedia_VerifiesSha256AndDecrypts`: Verifies chunk integrity verification on download.

### 5.3 Invitations & Query Tests (`Percolator.Apps.Chat.Tests/Invitations`)
- `GroupInvitationServiceTests.ReceiveInvitation_EmitsPendingInvitation`: Verifies pending invitation creation.
- `GroupInvitationServiceTests.AcceptInvitation_JoinsDomainGroup`: Verifies domain group aggregate mutation.
- `GroupInvitationServiceTests.DeclineInvitation_MarksDeclinedAndSuppressesFutureTraffic`: Verifies rejection flow.
- `InMemoryChatMessageQueryServiceTests.GetPagedMessagesAsync_ReturnsRequestedPage_WithoutDomainHydration`: Verifies CQRS fast-path read model queries.
- `InMemoryConversationListQueryServiceTests.GetRecentConversationsAsync_ComputesUnreadCountsCorrectly`: Verifies unread badge computation.

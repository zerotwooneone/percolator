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
    - **Domain Channels (Membership, Epochs, Sequence)**: `Percolator.Domain.Channels` (`DirectChannel` and `GroupChannel`) is the authoritative owner of channel membership, administrative roles, cryptographic epochs, and the chronological payload timeline. Every sent or received chat frame appends a `ChannelPayload` aggregate entry committed via `IChannelRepository`.
    - **Chat Message Persistence & Read Models**: Rich message state (markdown content, mentions, reactions, edit status, read receipts, tombstones) is maintained through `IChatMessageRepository`. Fast-path read operations (`IChatMessageQueryService` and `IConversationListQueryService`) query indexed database projections directly, retrieving paged timeline fields with sub-millisecond latency without hydrating heavy domain aggregates.
  - **Cryptographic Separation of Concerns**:
    - Sender key ratchet generation and distribution across 1:1 pairwise channels are owned by `Percolator.Application` (`GroupKeyDistributionService`) via `AppId.SystemControl` (0x00). `Percolator.Apps.Chat` operates strictly as an application payload consumer on top of established channels.
  - **Cryptographic Logging Guardrails**:
    - The Chat application strictly honors cryptographic logging guardrails (`CryptographyOptions.EnableCryptographicMaterialLogging = false` by default).
    - Diagnostic, audit, and trace logging must **never** record sensitive cryptographic key material (chain keys, message keys, symmetric media keys, or key derivation hashes) or user plaintexts. Only sanitized operational metadata (e.g. channel IDs, timestamps, payload size) may appear in logs.
  - Test-first implementation: All behaviors must have corresponding unit tests in `Percolator.Apps.Chat.Tests` using in-memory test doubles.

---

## 2. Signal-Aligned Cryptographic & Privacy Guardrails

To match the operational security and metadata resistance of the Signal Protocol, the wire protocol and payload processing enforce six strict privacy constraints:

1. **Traffic Analysis Defense via Stepped Bucket Padding**:
   - Stream ciphers (AES-GCM / ChaCha20-Poly1305) produce ciphertext of length equal to plaintext + 16-byte tag. Raw message lengths leak linguistic structure, language model patterns, and message type distinctions (e.g. distinguishing a 1-character reaction from a short text).
   - `ChatPayloadEnvelopeDto` mandates a pseudo-random `Padding` byte span that pads the serialized payload up to discrete stepped boundaries ($256\text{ B}$, $512\text{ B}$, $1024\text{ B}$, $2048\text{ B}$, $4096\text{ B}$).
2. **Reply Deniability & Anti-Tombstone Leakage**:
   - Quoting plaintext snippets over the wire breaks cryptographic deniability and permanently preserves retracted text: if Bob retracts a sensitive message via a tombstone, Alice's reply must not preserve Bob's deleted text in a quote transcript.
   - `ReplyContextDto` carries **only** `(PayloadId ParentPayloadId, PublicIdentityId ParentAuthorId)`. The receiving client resolves the parent message and snippet purely from its local `IChatMessageRepository`.
3. **Timestamp Clamping & Clock-Skew Fingerprinting Defense**:
   - Sub-millisecond .NET `DateTimeOffset` precision leaks operating system hardware clock tick rates, NTP synchronization jitter, and machine-specific drift, enabling cross-session identity linking.
   - All wire timestamps are normalized and clamped to integer Unix millisecond precision (`ulong TimestampUnixMs`), discarding OS tick fractions.
4. **UTF-8 Byte-Span Mention Invariant**:
   - Mention spans `(StartOffset, Length)` are measured strictly in **UTF-8 byte offsets** (`Encoding.UTF8.GetByteCount`), not C# UTF-16 characters (`string.Length`). This prevents string slicing corruption and off-by-one rendering flaws when emojis, surrogate pairs, or complex multi-byte scripts precede mentions.
   - Mentions carry `PublicIdentityId`, never display names or nicknames, avoiding identity spoofing and wire metadata leakage.
5. **Media Metadata & Filename Scrubbing**:
   - Camera and OS generated filenames (e.g., `IMG_20261010_180512.jpg`, `Screenshot_iPhone.png`) leak exact capture time, camera apps, and device hardware.
   - Photo, video, and audio attachments scrub original filenames client-side before dispatch (replacing with generic descriptors or omitting entirely). Only explicitly designated user file attachments retain sanitized filenames.
   - All out-of-band media blobs enforce EXIF/geolocation scrubbing and stepped chunk padding via `IBlobStorageService`.
6. **Strict Sealed Sender & Relay Isolation**:
   - The outer transport envelope (`SealedEnvelopeProto`) visible to relays contains **zero sender identity fields** and **zero channel IDs**.
   - `ChannelId`, `AuthorId`, and application payloads exist **exclusively inside Tier 3 (the inner E2EE Double Ratchet payload)**, preventing relays from constructing social graphs or mapping channel conversations.

---

## 3. Milestone 1: Chat Plugin Architecture, Binary DTOs & Wire Envelopes

### 3.1 Wire Envelope Discriminator & DTOs
All chat traffic over the wire is packaged in an explicit typed envelope to guarantee zero-ambiguity binary deserialization:

- **`ChatPayloadType` (Byte Discriminator)**:
  ```csharp
  public enum ChatPayloadType : byte
  {
      Message = 1,
      Edit = 2,
      Tombstone = 3,
      Reaction = 4,
      ReadReceipt = 5,
      SystemNotice = 6,
      GroupInvitation = 7,
      GroupInvitationResponse = 8
  }
  ```

- **`ChatPayloadEnvelopeDto`**:
  - `ChatPayloadType Type`: Discriminator identifying the inner payload.
  - `ReadOnlyMemory<byte> Data`: Serialized inner DTO bytes.
  - `ReadOnlyMemory<byte> Padding`: Random padding bytes expanding the total envelope to the nearest stepped boundary (256, 512, 1024, 2048, 4096 bytes).

- **Inner Application Payload DTOs** (Serialized via `IPayloadSerializer`):
  - `ChatMessageDto`:
    - `PayloadId Id`: Unique message identifier.
    - `ChannelId ChannelId`: Target direct or group channel.
    - `PublicIdentityId AuthorId`: Sender identity.
    - `ulong TimestampUnixMs`: Normalized Unix millisecond timestamp.
    - `string Content`: Rich text content (Markdown format).
    - `IReadOnlyList<MentionDto> Mentions`: Byte-offset mentions (`IdentityId`, `StartOffset`, `Length`).
    - `ReplyContextDto? ReplyContext`: Reply reference (`ParentPayloadId`, `ParentAuthorId`). No plaintext snippet on wire.
    - `LinkPreviewDto? LinkPreview`: Sender-generated client-side link preview.
    - `IReadOnlyList<MediaAttachmentDto> Attachments`: Inline or out-of-band media descriptors.
    - `int? ExpireAfterSeconds`: Disappearing message timer (null = permanent).
    - `ulong? EditedAtUnixMs`: Last edit timestamp (null = original).
  - `MessageEditDto`:
    - `PayloadId TargetPayloadId`: Message being edited.
    - `ChannelId ChannelId`: Target channel.
    - `PublicIdentityId AuthorId`: Editing identity.
    - `string NewContent`: Updated Markdown text.
    - `IReadOnlyList<MentionDto> NewMentions`: Updated mentions.
    - `ulong EditedAtUnixMs`: Edit timestamp.
  - `MessageTombstoneDto`:
    - `PayloadId TargetPayloadId`: Message being deleted.
    - `ChannelId ChannelId`: Target channel.
    - `PublicIdentityId RequestedBy`: Identity requesting deletion.
    - `ulong DeletedAtUnixMs`: Deletion timestamp.
  - `ReactionUpdateDto`:
    - `PayloadId TargetPayloadId`: Target message identifier.
    - `ChannelId ChannelId`: Target channel.
    - `PublicIdentityId AuthorId`: Reactor identity.
    - `string Emoji`: Unicode emoji representation.
    - `ReactionAction Action`: `Add` or `Remove`.
    - `ulong TimestampUnixMs`: Action timestamp.
  - `ReadReceiptDto`:
    - `ChannelId ChannelId`: Target channel.
    - `PublicIdentityId ReaderId`: Reading identity.
    - `PayloadId UpToPayloadId`: Highest read message sequence marker.
    - `ulong ReadAtUnixMs`: Read timestamp.
  - `SystemNoticeDto`:
    - `ChannelId ChannelId`: Target channel.
    - `SystemNoticeType Type`: `SafetyNumberChanged`, `MemberJoined`, `MemberLeft`, `RolePromoted`, `EpochAdvanced`.
    - `PublicIdentityId AffectedIdentity`: Identity involved.
    - `ulong TimestampUnixMs`: Notice timestamp.
  - `GroupInvitationDto`:
    - `Guid InvitationId`: Unique invitation identifier.
    - `ChannelId TargetGroupChannelId`: Target group channel ID.
    - `string GroupName`: Human-readable group name.
    - `PublicIdentityId InviterId`: Admin inviting the peer.
    - `string? RelayEndpoint`: Designated group common relay (if applicable).
    - `ulong ExpiresAtUnixMs`: Invitation validity window.
  - `GroupInvitationResponseDto`:
    - `Guid InvitationId`: Invitation identifier.
    - `ChannelId TargetGroupChannelId`: Target group channel ID.
    - `PublicIdentityId ResponderId`: Accepting or declining identity.
    - `bool Accepted`: True if accepted, false if declined.
    - `ulong RespondedAtUnixMs`: Response timestamp.

### 3.2 Rich Text Formatting & Mention Offset Invariants
- **Markdown Formatting**:
  - The UI parser supports bold (`**`), italic (`*`), strikethrough (`~~`), inline code (`` ` ``), blockquotes (`>`), and syntax-highlighted code blocks (` ``` `).
- **Byte-Span Mentions**:
  - Mentions are represented as `MentionDto(PublicIdentityId IdentityId, int StartOffset, int Length)` measured strictly in UTF-8 byte spans (`Encoding.UTF8.GetByteCount`).
  - Eliminates display name spoofing, character-boundary desync, and brittle raw-text regex parsing.

---

## 4. Milestone 2: Outbound Commands, Inbound Handler & Persistence Ports

### 4.1 Outbound Chat Service (`IChatService` & `ChatService`)
The outbound service invoked by the UI/Desktop client:
- **Port Interface**:
  ```csharp
  public interface IChatService
  {
      ValueTask<DomainResult<PayloadId>> SendMessageAsync(SendMessageCommand command, CancellationToken ct = default);
      ValueTask<DomainResult> EditMessageAsync(EditMessageCommand command, CancellationToken ct = default);
      ValueTask<DomainResult> DeleteMessageAsync(DeleteMessageCommand command, CancellationToken ct = default);
      ValueTask<DomainResult> AddReactionAsync(AddReactionCommand command, CancellationToken ct = default);
      ValueTask<DomainResult> RemoveReactionAsync(RemoveReactionCommand command, CancellationToken ct = default);
      ValueTask<DomainResult> MarkAsReadAsync(MarkAsReadCommand command, CancellationToken ct = default);
  }
  ```
- **Outbound Orchestration Pipeline**:
  1. **Validation**: Validates content limits ($\le$ 64 KB total envelope), UTF-8 mention byte boundaries, and active channel membership.
  2. **Attachment Pre-processing & Sanitization**: For media attachments, sanitizes filenames, strips EXIF data, coordinates stepped padding with `IBlobStorageService` (stages locally for 1:1 direct peer pull or uploads to channel relay).
  3. **DTO Creation & Serialization**: Creates the typed DTO with clamped Unix millisecond timestamps, serializes via `IPayloadSerializer`, calculates stepped padding bucket, and wraps in `ChatPayloadEnvelopeDto`.
  4. **Domain Channel Logging**: Appends `ChannelPayload` to `DirectChannel` or `GroupChannel` via `IChannelRepository`.
  5. **Network Dispatch**: Passes payload to `IPayloadSender.SendPayloadAsync(OutboundPayloadContext)`.
  6. **Local Chat Persistence**: Saves rich record in `IChatMessageRepository`.

### 4.2 Inbound Chat Payload Handler (`ChatPayloadHandler`)
- Implements `IAppPayloadHandler` for `AppId.Chat`.
- **Inbound Processing Flow**:
  1. Deserializes `ChatPayloadEnvelopeDto` via `IPayloadSerializer` (discards padding span).
  2. Routes by `envelope.Type`:
     - `Message`: Deserializes `ChatMessageDto`, validates author against channel membership, appends `ChannelPayload` to `IChannelRepository`, inserts into `IChatMessageRepository`, and triggers notification events.
     - `Edit`: Deserializes `MessageEditDto`, validates that author matches original message author and that `now - message.CreatedAt <= 24 hours`, updates `IChatMessageRepository`.
     - `Tombstone`: Deserializes `MessageTombstoneDto`, verifies author or channel admin permissions, updates `IChatMessageRepository` to mark tombstone placeholder, purges local media blob cache.
     - `Reaction`: Deserializes `ReactionUpdateDto`, idempotently records or removes reaction in `IChatMessageRepository`.
     - `ReadReceipt`: Deserializes `ReadReceiptDto`, updates remote read marker in `IChatMessageRepository`.
     - `GroupInvitation`: Deserializes `GroupInvitationDto`, creates `PendingGroupInvitation` in `IGroupInvitationRepository`.
     - `GroupInvitationResponse`: Deserializes `GroupInvitationResponseDto`, notifies group admin.

### 4.3 Persistence Ports (`Percolator.Apps.Chat.Ports`)
Pure C# repository ports implemented by `Percolator.Infrastructure2`:

- **`IChatMessageRepository`**:
  ```csharp
  public interface IChatMessageRepository
  {
      ValueTask SaveMessageAsync(ChatMessageRecord record, CancellationToken ct = default);
      ValueTask<ChatMessageRecord?> GetMessageByIdAsync(PayloadId payloadId, CancellationToken ct = default);
      ValueTask UpdateMessageEditAsync(PayloadId payloadId, string newContent, IReadOnlyList<MentionDto> mentions, DateTimeOffset editedAtUtc, CancellationToken ct = default);
      ValueTask ApplyTombstoneAsync(PayloadId payloadId, PublicIdentityId requestedBy, DateTimeOffset deletedAtUtc, CancellationToken ct = default);
      ValueTask AddReactionAsync(PayloadId payloadId, PublicIdentityId reactorId, string emoji, DateTimeOffset timestampUtc, CancellationToken ct = default);
      ValueTask RemoveReactionAsync(PayloadId payloadId, PublicIdentityId reactorId, string emoji, CancellationToken ct = default);
      ValueTask UpdateReadReceiptAsync(ChannelId channelId, PublicIdentityId peerId, PayloadId upToPayloadId, DateTimeOffset readAtUtc, CancellationToken ct = default);
      ValueTask PurgeExpiredMessagesAsync(DateTimeOffset nowUtc, CancellationToken ct = default);
  }
  ```

- **`IGroupInvitationRepository`**:
  ```csharp
  public interface IGroupInvitationRepository
  {
      ValueTask SaveInvitationAsync(PendingGroupInvitation invitation, CancellationToken ct = default);
      ValueTask<PendingGroupInvitation?> GetByIdAsync(Guid invitationId, CancellationToken ct = default);
      ValueTask UpdateStatusAsync(Guid invitationId, GroupInvitationStatus status, CancellationToken ct = default);
  }
  ```

- **`IUnknownGroupMessageCacheRepository`**:
  ```csharp
  public interface IUnknownGroupMessageCacheRepository
  {
      ValueTask StageUnknownMessageAsync(UnknownGroupMessageRecord record, CancellationToken ct = default);
      ValueTask<IReadOnlyList<UnknownGroupMessageRecord>> GetPendingByAuthorAsync(ChannelId channelId, PublicIdentityId authorId, CancellationToken ct = default);
      ValueTask RemoveAsync(Guid recordId, CancellationToken ct = default);
  }
  ```

### 4.4 Fast-Path Read Query Ports (`Percolator.Apps.Chat.Ports`)
- **`IChatMessageQueryService`**:
  - Read-only query port for paginated conversation messages, reactions, and attachments directly from database projections.
  - `Task<IReadOnlyList<ChatMessageReadModel>> GetPagedMessagesAsync(ChannelId channelId, int beforeSequence, int limit, CancellationToken ct = default);`
  - `Task<ChatMessageReadModel?> GetMessageByIdAsync(PayloadId payloadId, CancellationToken ct = default);`
- **`IConversationListQueryService`**:
  - Read-only query port for the user's conversation list feed.
  - `Task<IReadOnlyList<ConversationSummaryReadModel>> GetRecentConversationsAsync(PublicIdentityId ownerId, CancellationToken ct = default);`
- **`IGroupInvitationQueryService`**:
  - Read-only query port for pending group invitations awaiting user consent.
  - `Task<IReadOnlyList<GroupInvitationSummaryReadModel>> GetPendingInvitationsAsync(PublicIdentityId recipientId, CancellationToken ct = default);`

---

## 5. Milestone 3: Media Attachments, Disappearing Messages & Group Governance

### 5.1 Media Attachment Strategy (Inline vs. Out-of-Band)
To satisfy the strict 64 KB Double Ratchet payload limit:
1. **Inline Media ($\le$ 32 KB)**:
   - Short voice clips, animated stickers, custom emojis, and low-resolution image previews / BlurHashes.
   - Encrypted directly within the E2EE channel payload via `MediaAttachmentDto.InlineBytes`.
   - Zero additional network requests required; available instantaneously on message arrival.
2. **Out-of-Band Encrypted Blobs (32 KB to 50 MB)**:
   - Photos, audio tracks, GIF animations, and short video clips.
   - **EXIF Geolocation Scrubbing**: All GPS coordinates, camera serial numbers, and device tags are stripped client-side before padding and encryption.
   - **Filename Scrubbing**: Default camera/OS filenames stripped; generic or omitted on wire.
   - **Stepped Bucket Padding**: Plaintext media is padded to discrete size buckets (< 1 MB to 32 KB; 1–10 MB to 256 KB; > 10 MB to 1 MB) before encryption.
   - **Chunked STREAM AEAD**: Encrypted in $64\text{ KB}$ chunks with sequential counter nonces and independent auth tags via `Percolator.PluginSdk.IBlobStorageService`.
   - **Audio Waveforms & Layout Stability**:
     - Voice notes embed normalized amplitude bytes (`Waveform`) inline in `BlobReference` for immediate waveform rendering without audio decoding.
     - Images and videos mandate `Width` and `Height` in `BlobReference` to eliminate Cumulative Layout Shift (CLS) in chat feeds.
   - `MediaAttachmentDto` carries the lightweight `BlobReference` containing symmetric key, base nonce, ciphertext SHA-256 digest, unpadded size, and storage mode.

### 5.2 Channel Topologies & Media Expectations
Media behavior is strictly driven by the channel's configured topology:
1. **1:1 Direct Channel (No Relay)**:
   - Media can **only** be transferred when **both peers are online concurrently**.
   - **Staged Pull Workflow**: Sender calls `UploadBlobAsync`, which encrypts and stages chunks in the sender's local disk store. The tiny `BlobReference` is sent via chat message. When the recipient receives the message, it pulls chunks directly from the sender over a dedicated peer stream.
   - If remote peer is offline, the Chat UI disables upload or marks status: *"Waiting for recipient to come online to stream media"*.
2. **1:1 Relayed Channel**:
   - The channel's designated relay acts as the single media storage endpoint.
   - Sender uploads ciphertext out-of-band to the channel relay. Recipient downloads asynchronously upon coming online.
3. **Group Channel**:
   - The group's single common relay acts as the authoritative media store for all group members (pure Signal model).
   - Sender uploads encrypted, padded blob **once** to group relay and distributes `BlobReference` via group Sender Key ratchet.
4. **Decoupled Performance Pipeline**:
   - Blob uploads/downloads run **out-of-band BEFORE `OutboundEgressPipeline`**, ensuring the ratchet lock (`IChannelLockService`) is never held during file uploads.

### 5.3 Privacy-Preserving Link Previews
- **Zero Recipient Deanonymization (Signal-Aligned Privacy)**:
  - The recipient client must **never** automatically scrape URLs or fetch OpenGraph metadata from the internet.
- **Sender-Generated Preview Model**:
  - The sender's client optionally generates OpenGraph metadata (`Title`, `Description`, `CanonicalUrl`) and a small encrypted thumbnail blob.
  - Previews are embedded in `LinkPreviewDto` within the E2EE ratchet envelope.
  - Recipient renders preview purely from received E2EE data without external HTTP calls.

### 5.4 Disappearing Messages Eviction Model
- Configured at channel level or overridden per message via `ExpireAfterSeconds`.
- **Sender Timer**: Starts counting down immediately upon message dispatch (`expires_at_utc = sent_at + ExpireAfterSeconds`).
- **Recipient Timer**: Starts counting down upon message read receipt or first display in the active viewport (`expires_at_utc = read_at + ExpireAfterSeconds`).
- **Periodic Background Hosted Worker (`DisappearingMessageEvictionWorker`)**:
  - Wakes up periodically (every 30 seconds).
  - Queries `IChatMessageRepository.PurgeExpiredMessagesAsync(now)`.
  - Invokes `IBlobStorageService.DeleteLocalCacheAsync` to purge associated media blobs from disk.
- **Query Filter Safeguard**:
  - `IChatMessageQueryService` filters `WHERE (expires_at_utc IS NULL OR expires_at_utc > @now)` as an immediate defense even before the periodic worker fires.

### 5.5 Group Invitation Consent State Machine
- **Transport Mechanism**:
  - Group invitations travel over the existing **1:1 Pairwise Direct Channel** (direct or relayed) between inviter and invitee as a `GroupInvitationDto`.
  - Strictly E2EE, mutually authenticated, and spam-protected by existing pairwise trust.
- **`PendingGroupInvitation` Entity**:
  - States: `Pending`, `Accepted`, `Declined`, `Expired`.
- **`GroupInvitationService`**:
  - On incoming `GroupInvitationDto`: Persists `PendingGroupInvitation` via `IGroupInvitationRepository` and fires UI event.
  - On User Acceptance:
    - Joins `GroupChannel` via domain aggregate `AddMember`.
    - Dispatches `GroupInvitationResponseDto(Accepted = true)` back over 1:1 pairwise channel.
    - Notifies `GroupKeyDistributionService` in `Percolator.Application`.
  - On User Decline:
    - Marks invitation declined in `IGroupInvitationRepository`.
    - Dispatches `GroupInvitationResponseDto(Accepted = false)` back over 1:1 pairwise channel.

---

## 6. Milestone 4: Test Suite Specification

### 6.1 Outbound Commands Tests (`Percolator.Apps.Chat.Tests/Outbound`)
- `ChatServiceTests.SendMessageAsync_ValidText_SerializesEnvelopeWithSteppedPaddingAndDispatches`: Verifies stepped padding bucket expansion and `IPayloadSender` dispatch.
- `ChatServiceTests.SendMessageAsync_ClampsTimestampToUnixMilliseconds`: Verifies sub-millisecond tick precision is stripped.
- `ChatServiceTests.SendMessageAsync_ReplyContext_ExcludesSnippetFromWire`: Verifies only parent ID and author ID are included.
- `ChatServiceTests.SendMessageAsync_MentionOffsets_CalculatesUtf8ByteSpans`: Verifies UTF-8 byte offset indexing with emojis.
- `ChatServiceTests.SendMessageAsync_ImageAttachment_SanitizesFileName`: Verifies camera filename scrubbing.
- `ChatServiceTests.EditMessageAsync_WithinWindow_DispatchesEditDto`: Verifies 24-hour edit window acceptance.
- `ChatServiceTests.EditMessageAsync_PastWindow_ReturnsValidationError`: Verifies rejection of edits past 24 hours.
- `ChatServiceTests.DeleteMessageAsync_Author_DispatchesTombstoneDto`: Verifies tombstone creation.
- `ChatServiceTests.AddReactionAsync_DispatchesReactionUpdateDto`: Verifies emoji reaction command dispatch.
- `ChatServiceTests.MarkAsReadAsync_AdvancesCursorAndDispatchesReadReceiptDto`: Verifies read receipt dispatch.

### 6.2 Ingress & Inbound Payload Tests (`Percolator.Apps.Chat.Tests/Ingress`)
- `ChatPayloadHandlerTests.HandleInboundAsync_TextMessage_AppendsPayloadToDomainChannelAndSavesRecord`: Verifies domain channel append + chat repo insert.
- `ChatPayloadHandlerTests.HandleInboundAsync_DiscardsSteppedPaddingCleanly`: Verifies padding does not affect inner deserialization.
- `ChatPayloadHandlerTests.HandleInboundAsync_WithMentions_ValidatesUtf8ByteBoundaries`: Verifies byte offset span validation.
- `ChatPayloadHandlerTests.HandleInboundAsync_EmojiReaction_AppliesReactionToChatRepo`: Verifies reaction aggregation delta.
- `ChatPayloadHandlerTests.HandleInboundAsync_MessageEdit_WithinWindow_UpdatesChatRepo`: Verifies edit update.
- `ChatPayloadHandlerTests.HandleInboundAsync_MessageEdit_NonAuthor_RejectsEdit`: Verifies non-author edit rejection.
- `ChatPayloadHandlerTests.HandleInboundAsync_MessageTombstone_ReplacesWithDeletedPlaceholder`: Verifies tombstone mutation and media cache eviction trigger.
- `ChatPayloadHandlerTests.HandleInboundAsync_CorruptedEnvelope_ReturnsDeserializationError`: Verifies robust error handling on corrupted data.

### 6.3 Media, Invitations & Query Tests (`Percolator.Apps.Chat.Tests/Features`)
- `ChatMediaServiceTests.SendMediaAsync_1to1Direct_WhenPeerOnline_StagesAndSendsReference`: Verifies local staging and peer pull in 1:1 direct.
- `ChatMediaServiceTests.SendMediaAsync_1to1Direct_WhenPeerOffline_ReturnsPeerOfflineError`: Verifies failure expectation when peer is offline in non-relayed 1:1.
- `ChatMediaServiceTests.SendMediaAsync_GroupChannel_UploadsOnceToCommonRelay`: Verifies single upload to group common relay.
- `GroupInvitationServiceTests.ReceiveInvitation_EmitsPendingInvitation`: Verifies pending invitation creation.
- `GroupInvitationServiceTests.AcceptInvitation_JoinsDomainGroup`: Verifies domain group aggregate mutation.
- `InMemoryChatMessageQueryServiceTests.GetPagedMessagesAsync_ReturnsRequestedPage_WithoutDomainHydration`: Verifies CQRS fast-path read model queries.
- `DisappearingMessageEvictionWorkerTests.ExecuteAsync_PurgesExpiredMessages`: Verifies background eviction worker purging.

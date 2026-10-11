# Percolator.Apps.Chat Implementation Plan

> [!NOTE]
> This is an ephemeral, step-by-step implementation plan. For domain requirements, threat models, and architectural invariants, refer to [spec.md](spec.md).

---

## 1. Project Structure & Namespaces

All code will reside in `Percolator.Apps.Chat` (`net10.0`), strictly referencing `Percolator.Domain` and `Percolator.PluginSdk`:

```
Percolator.Apps.Chat/
├── ChatPlugin.cs                                    # IAppPlugin implementation (AppId.Chat = 0x01)
├── DependencyInjection/
│   └── ServiceCollectionExtensions.cs              # DI registrations
├── Models/
│   ├── ChatPayloadType.cs                          # Byte discriminator enum
│   ├── ChatPayloadEnvelopeDto.cs                   # Typed wire container with padding
│   ├── ChatMessageDto.cs                           # Core message DTO
│   ├── MessageEditDto.cs                           # Edit DTO
│   ├── MessageTombstoneDto.cs                      # Deletion/tombstone DTO
│   ├── ReactionUpdateDto.cs                        # Reaction add/remove DTO
│   ├── ReadReceiptDto.cs                           # Read receipt DTO
│   ├── SystemNoticeDto.cs                          # In-timeline administrative notice DTO
│   ├── GroupInvitationDto.cs                       # Pairwise group invitation DTO
│   ├── GroupInvitationResponseDto.cs               # Pairwise invitation response DTO
│   ├── MentionDto.cs                               # UTF-8 byte span mention
│   ├── ReplyContextDto.cs                          # Reply reference (ID + author only)
│   ├── LinkPreviewDto.cs                           # Sender-generated OpenGraph preview
│   ├── MediaAttachmentDto.cs                       # Media descriptor
│   ├── PendingGroupInvitation.cs                   # Group invitation entity
│   ├── ChatMessageRecord.cs                        # Storage persistence record
│   ├── UnknownGroupMessageRecord.cs                # Out-of-order group message record
│   └── ReadModels/
│       ├── ChatMessageReadModel.cs                 # Projected message read model
│       ├── ConversationSummaryReadModel.cs         # Channel list summary
│       ├── ReactionGroupReadModel.cs               # Aggregated reaction read model
│       └── GroupInvitationSummaryReadModel.cs      # Invitation banner read model
├── Commands/
│   ├── SendMessageCommand.cs                       # IChatService send parameters
│   ├── EditMessageCommand.cs                       # IChatService edit parameters
│   ├── DeleteMessageCommand.cs                     # IChatService delete parameters
│   ├── AddReactionCommand.cs                       # IChatService reaction add parameters
│   ├── RemoveReactionCommand.cs                    # IChatService reaction remove parameters
│   └── MarkAsReadCommand.cs                        # IChatService read progress parameters
├── Ports/
│   ├── IChatService.cs                             # Outbound write service port
│   ├── IChatMessageRepository.cs                   # Rich message persistence port
│   ├── IGroupInvitationRepository.cs               # Invitation persistence port
│   ├── IUnknownGroupMessageCacheRepository.cs      # Out-of-order group frame cache
│   ├── IChatMessageQueryService.cs                 # Fast-path paged message query port
│   ├── IConversationListQueryService.cs            # Fast-path conversation feed query port
│   └── IGroupInvitationQueryService.cs             # Fast-path pending invitation query port
├── Services/
│   ├── ChatService.cs                              # Outbound command orchestrator
│   ├── ChatMediaService.cs                         # Media attachment coordinator
│   ├── GroupInvitationService.cs                   # Invitation state machine coordinator
│   └── SteppedPadding.cs                           # Padding calculation helper
├── Handlers/
│   └── ChatPayloadHandler.cs                       # IAppPayloadHandler for AppId.Chat
└── spec.md                                         # Long-term domain specification
```

---

## 2. Milestone 1: Wire DTOs, Envelopes & Models

### 2.1 Enums & Discriminator
- **`ChatPayloadType.cs`**:
  ```csharp
  namespace Percolator.Apps.Chat.Models;

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
- **`ReactionAction.cs`**: `public enum ReactionAction : byte { Add = 1, Remove = 2 }`
- **`SystemNoticeType.cs`**: `public enum SystemNoticeType : byte { SafetyNumberChanged = 1, MemberJoined = 2, MemberLeft = 3, RolePromoted = 4, EpochAdvanced = 5 }`
- **`GroupInvitationStatus.cs`**: `public enum GroupInvitationStatus : byte { Pending = 1, Accepted = 2, Declined = 3, Expired = 4 }`

### 2.2 Wire Envelope & Padding Helper
- **`SteppedPadding.cs`**:
  ```csharp
  namespace Percolator.Apps.Chat.Services;

  public static class SteppedPadding
  {
      public static int CalculatePaddedLength(int unpaddedLength)
      {
          if (unpaddedLength <= 256) return 256;
          if (unpaddedLength <= 512) return 512;
          if (unpaddedLength <= 1024) return 1024;
          if (unpaddedLength <= 2048) return 2048;
          if (unpaddedLength <= 4096) return 4096;
          return ((unpaddedLength + 4095) / 4096) * 4096;
      }
  }
  ```
- **`ChatPayloadEnvelopeDto.cs`**:
  ```csharp
  namespace Percolator.Apps.Chat.Models;

  public sealed record ChatPayloadEnvelopeDto(
      ChatPayloadType Type,
      ReadOnlyMemory<byte> Data,
      ReadOnlyMemory<byte> Padding);
  ```

### 2.3 Application Payload DTOs
All timestamps use `ulong TimestampUnixMs` (millisecond-precision Unix epoch).
- **`MentionDto.cs`**: `(PublicIdentityId IdentityId, int StartOffset, int Length)` where offsets are UTF-8 byte spans.
- **`ReplyContextDto.cs`**: `(PayloadId ParentPayloadId, PublicIdentityId ParentAuthorId)` - strictly no quote text on the wire.
- **`LinkPreviewDto.cs`**: `(string Title, string Description, string CanonicalUrl, BlobReference? ThumbnailBlob)`
- **`MediaAttachmentDto.cs`**: `(BlobId BlobId, string? FileName, string ContentType, long ByteSize, int? Width, int? Height, byte[]? Waveform, ReadOnlyMemory<byte>? InlineBytes)`
- **`ChatMessageDto.cs`**:
  ```csharp
  namespace Percolator.Apps.Chat.Models;

  public sealed record ChatMessageDto(
      PayloadId Id,
      ChannelId ChannelId,
      PublicIdentityId AuthorId,
      ulong TimestampUnixMs,
      string Content,
      IReadOnlyList<MentionDto> Mentions,
      ReplyContextDto? ReplyContext,
      LinkPreviewDto? LinkPreview,
      IReadOnlyList<MediaAttachmentDto> Attachments,
      int? ExpireAfterSeconds,
      ulong? EditedAtUnixMs);
  ```
- **`MessageEditDto.cs`**: `(PayloadId TargetPayloadId, ChannelId ChannelId, PublicIdentityId AuthorId, string NewContent, IReadOnlyList<MentionDto> NewMentions, ulong EditedAtUnixMs)`
- **`MessageTombstoneDto.cs`**: `(PayloadId TargetPayloadId, ChannelId ChannelId, PublicIdentityId RequestedBy, ulong DeletedAtUnixMs)`
- **`ReactionUpdateDto.cs`**: `(PayloadId TargetPayloadId, ChannelId ChannelId, PublicIdentityId AuthorId, string Emoji, ReactionAction Action, ulong TimestampUnixMs)`
- **`ReadReceiptDto.cs`**: `(ChannelId ChannelId, PublicIdentityId ReaderId, PayloadId UpToPayloadId, ulong ReadAtUnixMs)`
- **`SystemNoticeDto.cs`**: `(ChannelId ChannelId, SystemNoticeType Type, PublicIdentityId AffectedIdentity, ulong TimestampUnixMs)`
- **`GroupInvitationDto.cs`**: `(Guid InvitationId, ChannelId TargetGroupChannelId, string GroupName, PublicIdentityId InviterId, string? RelayEndpoint, ulong ExpiresAtUnixMs)`
- **`GroupInvitationResponseDto.cs`**: `(Guid InvitationId, ChannelId TargetGroupChannelId, PublicIdentityId ResponderId, bool Accepted, ulong RespondedAtUnixMs)`

### 2.4 Internal Records & Read Models
- **`ChatMessageRecord.cs`**: Represents the persisted message row in `IChatMessageRepository` (with `ExpiresAtUtc`, `IsDeleted`, `CreatedAtUtc`, reactions, mentions, attachments).
- **`PendingGroupInvitation.cs`**: Entity representing a received group invitation with state transition methods (`Accept()`, `Decline()`, `Expire()`).
- **`ChatMessageReadModel.cs`**: Read model for the UI containing formatted text, mentions, resolved parent snippet, attachments, and `ReactionGroupReadModel` aggregations.
- **`ConversationSummaryReadModel.cs`**: Read model for the conversations list (ChannelId, Title, Snippet, LastActivityUtc, UnreadCount).

---

## 3. Milestone 2: Ports (Repositories & Query Services)

Define interfaces in `Percolator.Apps.Chat.Ports`:

### 3.1 `IChatMessageRepository`
```csharp
namespace Percolator.Apps.Chat.Ports;

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

### 3.2 `IGroupInvitationRepository`
```csharp
namespace Percolator.Apps.Chat.Ports;

public interface IGroupInvitationRepository
{
    ValueTask SaveInvitationAsync(PendingGroupInvitation invitation, CancellationToken ct = default);
    ValueTask<PendingGroupInvitation?> GetByIdAsync(Guid invitationId, CancellationToken ct = default);
    ValueTask UpdateStatusAsync(Guid invitationId, GroupInvitationStatus status, CancellationToken ct = default);
}
```

### 3.3 `IUnknownGroupMessageCacheRepository`
```csharp
namespace Percolator.Apps.Chat.Ports;

public interface IUnknownGroupMessageCacheRepository
{
    ValueTask StageUnknownMessageAsync(UnknownGroupMessageRecord record, CancellationToken ct = default);
    ValueTask<IReadOnlyList<UnknownGroupMessageRecord>> GetPendingByAuthorAsync(ChannelId channelId, PublicIdentityId authorId, CancellationToken ct = default);
    ValueTask RemoveAsync(Guid recordId, CancellationToken ct = default);
}
```

### 3.4 Fast-Path Read Query Ports
- **`IChatMessageQueryService`**:
  ```csharp
  Task<IReadOnlyList<ChatMessageReadModel>> GetPagedMessagesAsync(ChannelId channelId, int beforeSequence, int limit, CancellationToken ct = default);
  Task<ChatMessageReadModel?> GetMessageByIdAsync(PayloadId payloadId, CancellationToken ct = default);
  ```
- **`IConversationListQueryService`**:
  ```csharp
  Task<IReadOnlyList<ConversationSummaryReadModel>> GetRecentConversationsAsync(PublicIdentityId ownerId, CancellationToken ct = default);
  ```
- **`IGroupInvitationQueryService`**:
  ```csharp
  Task<IReadOnlyList<GroupInvitationSummaryReadModel>> GetPendingInvitationsAsync(PublicIdentityId recipientId, CancellationToken ct = default);
  ```

---

## 4. Milestone 3: Services & Handlers

### 4.1 Outbound Commands & `ChatService`
- **Commands**:
  - `SendMessageCommand`: `(ChannelId ChannelId, PublicIdentityId AuthorId, DeviceId DeviceId, string Content, IReadOnlyList<MentionDto> Mentions, ReplyContextDto? ReplyContext, IReadOnlyList<MediaAttachmentDto> Attachments, int? ExpireAfterSeconds, DeliveryRoute Route)`
  - `EditMessageCommand`: `(ChannelId ChannelId, PayloadId TargetPayloadId, PublicIdentityId AuthorId, DeviceId DeviceId, string NewContent, IReadOnlyList<MentionDto> NewMentions, DeliveryRoute Route)`
  - `DeleteMessageCommand`: `(ChannelId ChannelId, PayloadId TargetPayloadId, PublicIdentityId RequestedBy, DeviceId DeviceId, DeliveryRoute Route)`
  - `AddReactionCommand`: `(ChannelId ChannelId, PayloadId TargetPayloadId, PublicIdentityId AuthorId, DeviceId DeviceId, string Emoji, DeliveryRoute Route)`
  - `RemoveReactionCommand`: `(ChannelId ChannelId, PayloadId TargetPayloadId, PublicIdentityId AuthorId, DeviceId DeviceId, string Emoji, DeliveryRoute Route)`
  - `MarkAsReadCommand`: `(ChannelId ChannelId, PublicIdentityId ReaderId, DeviceId DeviceId, PayloadId UpToPayloadId, DeliveryRoute Route)`

- **`ChatService` Implementation Logic**:
  1. Validates UTF-8 mention boundaries (`Encoding.UTF8.GetByteCount`) and ensures content is not whitespace-only.
  2. For `EditMessageAsync`: validates `target.AuthorId == command.AuthorId` and `now - target.CreatedAt <= 24 hours`.
  3. Clamps send timestamp: `(ulong)timeProvider.UtcNow.ToUnixTimeMilliseconds()`.
  4. Serializes the inner DTO with `IPayloadSerializer.Serialize(innerDto)`.
  5. Computes stepped padding: `SteppedPadding.CalculatePaddedLength(innerBytes.Length) - innerBytes.Length`.
  6. Wraps into `ChatPayloadEnvelopeDto(type, innerBytes, paddingBytes)` and serializes envelope.
  7. Appends `ChannelPayload` to domain aggregate (`DirectChannel` or `GroupChannel`) via `IChannelRepository`.
  8. Calls `IPayloadSender.SendPayloadAsync(outboundContext)` to ship over network.
  9. Commits the record to `IChatMessageRepository`.

### 4.2 Inbound `ChatPayloadHandler`
- Implements `IAppPayloadHandler` with `TargetAppId => AppId.Chat`.
- Logic:
  1. Deserializes `ChatPayloadEnvelopeDto` from `context.Payload`.
  2. Evaluates `envelope.Type`:
     - **`Message`**: Deserializes `ChatMessageDto`, appends `ChannelPayload` to `IChannelRepository`, inserts `ChatMessageRecord` into `IChatMessageRepository`.
     - **`Edit`**: Deserializes `MessageEditDto`. Verifies that author matches original message author and that edit window $\le 24\text{ hours}$. Updates `IChatMessageRepository`.
     - **`Tombstone`**: Deserializes `MessageTombstoneDto`. Verifies authorization. Updates `IChatMessageRepository` with deleted placeholder and purges associated media cache via `IBlobStorageService`.
     - **`Reaction`**: Deserializes `ReactionUpdateDto`. Calls `AddReactionAsync` or `RemoveReactionAsync` on `IChatMessageRepository`.
     - **`ReadReceipt`**: Deserializes `ReadReceiptDto`. Calls `UpdateReadReceiptAsync` on `IChatMessageRepository`.
     - **`GroupInvitation`**: Deserializes `GroupInvitationDto`. Creates `PendingGroupInvitation` in `IGroupInvitationRepository`.
     - **`GroupInvitationResponse`**: Deserializes `GroupInvitationResponseDto`.
     - **Unknown Type**: Forward-compatibility: logs debug trace and returns `DomainResult.Success()` without throwing.

### 4.3 `GroupInvitationService`
- Manages the consent lifecycle:
  - `AcceptInvitationAsync(Guid invitationId)`: Calls `invitation.Accept()`, adds member to `GroupChannel`, saves to `IChannelRepository`, dispatches `GroupInvitationResponseDto(Accepted = true)` over the pairwise channel.
  - `DeclineInvitationAsync(Guid invitationId)`: Calls `invitation.Decline()`, dispatches `GroupInvitationResponseDto(Accepted = false)` over the pairwise channel.

### 4.4 `ChatPlugin`
- Implements `IAppPlugin`:
  - `Id => AppId.Chat`
  - `Name => "Chat"`
  - `StartAsync` and `StopAsync` lifecycle hooks.

---

## 5. Milestone 4: Unit Test Suite (`Percolator.Apps.Chat.Tests`)

Implement full in-memory unit tests using mocks and test doubles:

### 5.1 Test Doubles
- `InMemoryChatMessageRepository`: Thread-safe dictionary-backed store implementing `IChatMessageRepository`.
- `InMemoryGroupInvitationRepository`: Thread-safe dictionary-backed store implementing `IGroupInvitationRepository`.
- `TestPayloadSender`: Captures dispatched `OutboundPayloadContext` for assertion.
- `TestPayloadSerializer`: Real implementation of `IPayloadSerializer` for roundtrip testing.
- `TestDateTimeProvider`: Controllable time provider for testing 24-hour edit windows and timestamps.

### 5.2 Test Cases
1. **Outbound Commands**:
   - `SendMessageAsync_ValidText_SerializesEnvelopeWithSteppedPaddingAndDispatches`
   - `SendMessageAsync_ClampsTimestampToUnixMilliseconds`
   - `SendMessageAsync_ReplyContext_ExcludesSnippetFromWire`
   - `SendMessageAsync_MentionOffsets_CalculatesUtf8ByteSpans`
   - `EditMessageAsync_WithinWindow_DispatchesEditDto`
   - `EditMessageAsync_PastWindow_ReturnsValidationError`
   - `DeleteMessageAsync_Author_DispatchesTombstoneDto`
   - `AddReactionAsync_DispatchesReactionUpdateDto`
   - `MarkAsReadAsync_AdvancesCursorAndDispatchesReadReceiptDto`
2. **Inbound Payloads**:
   - `HandleInboundAsync_TextMessage_AppendsPayloadToDomainChannelAndSavesRecord`
   - `HandleInboundAsync_DiscardsSteppedPaddingCleanly`
   - `HandleInboundAsync_WithMentions_ValidatesUtf8ByteBoundaries`
   - `HandleInboundAsync_EmojiReaction_AppliesReactionToChatRepo`
   - `HandleInboundAsync_MessageEdit_WithinWindow_UpdatesChatRepo`
   - `HandleInboundAsync_MessageEdit_NonAuthor_RejectsEdit`
   - `HandleInboundAsync_MessageTombstone_ReplacesWithDeletedPlaceholder`
   - `HandleInboundAsync_CorruptedEnvelope_ReturnsDeserializationError`
   - `HandleInboundAsync_FutureUnknownType_ReturnsSuccessWithoutError`
3. **Invitations**:
   - `ReceiveInvitation_CreatesPendingRecord`
   - `AcceptInvitation_JoinsGroupChannelAndDispatchesResponse`
   - `DeclineInvitation_MarksDeclinedAndDispatchesDeclineResponse`

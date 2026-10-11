# Percolator.Apps.Chat Feature Slice Implementation Plan (Option A)

> [!NOTE]
> This implementation plan executes **Vertical Feature Slice Architecture (Option A)** within `Percolator.Apps.Chat`.
> It organizes code into cohesive functional feature slices (`Messaging`, `Reactions`, `GroupInvitations`, `Media`, and `Ingress`) using standard 1:1 .NET folder-to-namespace mappings, implements Signal-aligned group administrator message retractions, and enforces strict Clean/Onion boundaries with zero pollution in `Percolator.Domain`.

---

## 1. Architectural Strategy & Design Principles

### 1.1 Context Boundary & Separation of Concerns
- **`Percolator.Domain` (Untouched Substrate Domain)**:
  - Models the core cryptographic substrate: ratchets, sessions, sender keys, and channel payload sequencing (`DirectChannel`, `GroupChannel`, `ChannelPayload`).
  - Contains zero chat-specific concepts (no markdown, mentions, reactions, edit windows, or invitations).
  - Referenced by `Percolator.Apps.Chat` strictly as a **Shared Kernel** for foundational identity/channel value objects: `ChannelId`, `PublicIdentityId`, `DeviceId`, `PayloadId`, `DomainResult`, `IDateTimeProvider`.
- **`Percolator.Apps.Chat` (Autonomous Feature-Sliced Bounded Context)**:
  - Organized vertically into 4 functional feature slices: `Messaging`, `Reactions`, `GroupInvitations`, and `Media`, tied together by an `Ingress` payload dispatcher.
  - Standard 1:1 .NET directory-to-namespace structure:
    - `Percolator.Apps.Chat.Features.Messaging`
    - `Percolator.Apps.Chat.Features.Reactions`
    - `Percolator.Apps.Chat.Features.GroupInvitations`
    - `Percolator.Apps.Chat.Features.Media`
    - `Percolator.Apps.Chat.Ingress`
  - Wire DTOs are strictly decoupled from internal domain state (e.g. `ChatReaction` value object replaces `ReactionUpdateDto` in entity state).

### 1.2 Signal-Aligned Group Admin Tombstone Model
- **Author Deletion**:
  - In 1:1 and Group channels, the original message author can tombstone/retract their message within 24 hours of dispatch.
  - Resulting content: `"This message was deleted"`.
- **Admin Deletion (Group Channels Only)**:
  - A participant with `ChannelRole.Admin` in the target `GroupChannel` can tombstone any participant's message within 24 hours of dispatch.
  - Resulting content: `"This message was deleted by a channel administrator"`.
  - The entity records `DeletedBy = requestedBy` and `DeletedAtUnixMs` for auditability and transparency.
  - In 1:1 channels, admin deletions do not exist; deletions are strictly author-only.

### 1.3 Testing Discipline (per `unit-testing.md`)
- **Strict TDD (Red-Green-Refactor)**: Write failing tests asserting domain invariants and security guards before adjusting logic.
- **High-Value Defect Prevention**: Verify temporal limits (24h edit/tombstone windows), identity consistency, cross-channel isolation, and replay idempotency.
- **Virtualized Time**: Clock evaluations strictly run through `FakeDateTimeProvider` (no wall-clock dependencies).
- **No Ritualistic Boilerplate**: Zero tests for trivial getters, record equality, or constructor assignments.

---

## 2. Target File & Namespace Layout (Option A)

```
Percolator.Apps.Chat/
├── Features/
│   ├── Messaging/                               # Namespace: Percolator.Apps.Chat.Features.Messaging
│   │   ├── ChatMessage.cs                       # Rich Domain Entity (Creation, 24h edits & tombstones)
│   │   ├── ChatMessageDto.cs                   # Wire message DTO
│   │   ├── MessageEditDto.cs                   # Wire edit DTO
│   │   ├── MessageTombstoneDto.cs              # Wire retraction DTO
│   │   ├── ChatPayloadEnvelopeDto.cs           # Wire envelope with stepped padding
│   │   ├── ChatPayloadType.cs                  # 1-byte wire discriminator enum
│   │   ├── MentionDto.cs                       # Wire mention span (UTF-8 byte offsets)
│   │   ├── ReplyContextDto.cs                  # Wire reply reference (parent ID + author only)
│   │   ├── LinkPreviewDto.cs                   # Wire sender-generated preview metadata
│   │   ├── ReadReceiptDto.cs                   # Wire read cursor progression marker
│   │   ├── SystemNoticeDto.cs                  # Wire system notice DTO
│   │   ├── SystemNoticeType.cs                 # System notice type enum
│   │   ├── SendMessageCommand.cs               # Outbound send command
│   │   ├── EditMessageCommand.cs               # Outbound edit command
│   │   ├── DeleteMessageCommand.cs             # Outbound delete command
│   │   ├── MarkAsReadCommand.cs                # Outbound read receipt command
│   │   ├── ChatMessageReadModel.cs             # Projected timeline item
│   │   ├── ConversationSummaryReadModel.cs     # Projected channel feed entry
│   │   ├── IChatMessageRepository.cs           # Persistence write port (persists ChatMessage entity)
│   │   ├── IChatMessageQueryService.cs         # Paged query read port
│   │   ├── IConversationListQueryService.cs    # Channel summary query read port
│   │   ├── IChatService.cs                     # Application write service port
│   │   ├── ChatService.cs                      # Application write orchestrator
│   │   ├── SteppedPadding.cs                   # Traffic analysis padding service
│   │   └── MentionHelper.cs                    # UTF-8 byte span indexing service
│   │
│   ├── Reactions/                               # Namespace: Percolator.Apps.Chat.Features.Reactions
│   │   ├── ChatReaction.cs                     # Domain Value Object (ReactorId, Emoji, ReactedAtUnixMs)
│   │   ├── ReactionUpdateDto.cs                # Wire reaction DTO
│   │   ├── ReactionAction.cs                   # Wire action enum (Add, Remove)
│   │   ├── AddReactionCommand.cs               # Outbound add reaction command
│   │   ├── RemoveReactionCommand.cs            # Outbound remove reaction command
│   │   └── ReactionGroupReadModel.cs           # Projected aggregate reaction badge
│   │
│   ├── GroupInvitations/                        # Namespace: Percolator.Apps.Chat.Features.GroupInvitations
│   │   ├── PendingGroupInvitation.cs           # Domain Entity (state machine: Pending -> Accepted/Declined/Expired)
│   │   ├── GroupInvitationDto.cs               # Wire invitation DTO
│   │   ├── GroupInvitationResponseDto.cs       # Wire response DTO
│   │   ├── GroupInvitationStatus.cs            # Wire/Domain status enum
│   │   ├── GroupInvitationSummaryReadModel.cs  # Projected pending invitation banner
│   │   ├── IGroupInvitationRepository.cs       # Persistence write port
│   │   ├── IGroupInvitationQueryService.cs     # Pending invites read port
│   │   └── GroupInvitationService.cs           # Pairwise consent application coordinator
│   │
│   └── Media/                                   # Namespace: Percolator.Apps.Chat.Features.Media
│       ├── MediaAttachmentDto.cs               # Wire media descriptor (inline bytes or blob reference)
│       └── ChatMediaService.cs                 # 32 KB threshold & filename privacy scrubbing service
│
├── Ingress/                                     # Namespace: Percolator.Apps.Chat.Ingress
│   ├── ChatPayloadHandler.cs                   # IAppPayloadHandler (dispatches wire packets to slices)
│   ├── UnknownGroupMessageRecord.cs            # Out-of-order frame cache DTO
│   └── IUnknownGroupMessageCacheRepository.cs  # Out-of-order frame cache port
│
├── ChatPlugin.cs                               # IAppPlugin entrypoint (AppId.Chat = 0x01)
├── DependencyInjection/
│   └── ServiceCollectionExtensions.cs          # DI registration extension
└── spec.md                                     # Long-term domain specification
```

---

## 3. Implementation Milestones

### Milestone 1: Slice Reorganization & Rich Domain Models (TDD)

#### Step 1.1: `Features/Reactions`
- **Domain Value Object**:
  `namespace Percolator.Apps.Chat.Features.Reactions;`
  `public sealed record ChatReaction(PublicIdentityId ReactorId, string Emoji, ulong ReactedAtUnixMs);`
  - Immutable value object representing an emoji reaction on a message.
  - Replaces `ReactionUpdateDto` inside the domain entity state.
- **Move Files & Update Namespaces**:
  - `ReactionUpdateDto.cs`, `ReactionAction.cs`, `AddReactionCommand.cs`, `RemoveReactionCommand.cs`, `ReactionGroupReadModel.cs` into `Features/Reactions/`.
- **TDD Tests**:
  - `Percolator.Apps.Chat.Tests/Features/Reactions/ChatReactionTests.cs`.

#### Step 1.2: `Features/Messaging`
- **Domain Entity: `ChatMessage`** (Refactored from `ChatMessageRecord`):
  `namespace Percolator.Apps.Chat.Features.Messaging;`
  Encapsulates invariants returning `DomainResult`:
  - `ApplyEdit(string newContent, IReadOnlyList<MentionDto> newMentions, ulong editedAtUnixMs, PublicIdentityId editorId)`:
    - Asserts `!IsTombstone` $\rightarrow$ `DomainError("ALREADY_TOMBSTONED")`.
    - Asserts `editorId == AuthorId` $\rightarrow$ `DomainError("UNAUTHORIZED_AUTHOR")`.
    - Asserts `editedAtUnixMs - TimestampUnixMs <= 24h` $\rightarrow$ `DomainError("EDIT_WINDOW_EXPIRED")`.
  - `ApplyTombstone(PublicIdentityId requestedBy, ulong deletedAtUnixMs, bool isChannelAdmin = false)`:
    - Asserts `!IsTombstone` $\rightarrow$ `DomainError("ALREADY_TOMBSTONED")`.
    - Asserts `requestedBy == AuthorId || isChannelAdmin` $\rightarrow$ `DomainError("UNAUTHORIZED_AUTHOR")`.
    - Asserts `deletedAtUnixMs - TimestampUnixMs <= 24h` $\rightarrow$ `DomainError("TOMBSTONE_WINDOW_EXPIRED")`.
    - Content replaced:
      `requestedBy != AuthorId && isChannelAdmin ? "This message was deleted by a channel administrator" : "This message was deleted"`
    - Clears attachments, wipes link previews, sets `IsTombstone = true`, records `DeletedBy = requestedBy` and `DeletedAtUnixMs = deletedAtUnixMs`.
  - `AddReaction(PublicIdentityId reactorId, string emoji, ulong timestampUnixMs)`: Idempotently tracks reaction in `List<ChatReaction>`.
  - `RemoveReaction(PublicIdentityId reactorId, string emoji)`: Idempotently removes reaction.
  - `IsExpired(ulong currentTimestampUnixMs)`: Evaluates TTL expiration.
- **Move Files & Update Namespaces**:
  - Move messaging DTOs, commands, read models, repositories, and services into `Features/Messaging/`.
- **TDD Tests**:
  - `Percolator.Apps.Chat.Tests/Features/Messaging/ChatMessageTests.cs` verifying 24h edit window, author/admin tombstone authorization, and reaction mutations.

#### Step 1.3: `Features/GroupInvitations`
- **Move Files & Update Namespaces**:
  - Move `PendingGroupInvitation.cs`, `GroupInvitationDto.cs`, `GroupInvitationResponseDto.cs`, `GroupInvitationStatus.cs`, `GroupInvitationSummaryReadModel.cs`, `IGroupInvitationRepository.cs`, `IGroupInvitationQueryService.cs`, `GroupInvitationService.cs` into `Features/GroupInvitations/`.
- **TDD Tests**:
  - Move and update tests to `Percolator.Apps.Chat.Tests/Features/GroupInvitations/`.

#### Step 1.4: `Features/Media`
- **Move Files & Update Namespaces**:
  - Move `MediaAttachmentDto.cs` and `ChatMediaService.cs` into `Features/Media/`.
- **TDD Tests**:
  - Move and update tests to `Percolator.Apps.Chat.Tests/Features/Media/`.

---

### Milestone 2: Security Hardening & Ingress Dispatch (TDD)

#### Step 2.1: Ingress Organization
- Move `ChatPayloadHandler.cs`, `UnknownGroupMessageRecord.cs`, and `IUnknownGroupMessageCacheRepository.cs` into `Ingress/` (`namespace Percolator.Apps.Chat.Ingress;`).

#### Step 2.2: Patch Security Vulnerabilities in `ChatPayloadHandler`
1. **Reaction Author Verification**:
   - In `HandleReactionAsync`: Require `dto.AuthorId == context.SenderIdentityId`. Reject with `DomainError("UNAUTHORIZED_AUTHOR", "Reaction author does not match cryptographic sender.")`.
2. **Cross-Channel Isolation (Confused Deputy Guard)**:
   - In `HandleMessageAsync`: Assert `dto.ChannelId == context.ChannelId`.
   - In `HandleEditAsync`, `HandleTombstoneAsync`, `HandleReactionAsync`:
     - Assert `dto.ChannelId == context.ChannelId`.
     - Assert `record.ChannelId == context.ChannelId`.
     - Reject with `DomainError("CHANNEL_MISMATCH", "Target message belongs to a different channel.")` if mismatched.
3. **Replay & Duplicate Ingestion Guard**:
   - In `HandleMessageAsync`: Query `await _chatRepo.GetByIdAsync(dto.Id, ct)`.
   - If message exists: return `DomainResult.Success()` idempotently without overwriting existing state or tombstones.
4. **Invitation Impersonation Prevention**:
   - In `HandleGroupInvitationAsync`: Assert `dto.InviterId == context.SenderIdentityId`.
   - In `HandleGroupInvitationResponseAsync`: Assert `dto.ResponderId == context.SenderIdentityId`.
5. **Signal-Aligned Group Admin Tombstone Verification**:
   - In `HandleTombstoneAsync`:
     - If `context.SenderIdentityId == record.AuthorId`: call `record.ApplyTombstone(context.SenderIdentityId, dto.DeletedAtUnixMs, isChannelAdmin: false)`.
     - Else: look up `groupChannel` via `_channelRepo.GetGroupByIdAsync(context.ChannelId)`. Check if `groupChannel?.Members.FirstOrDefault(m => m.Id == context.SenderIdentityId)?.Role == ChannelRole.Admin`.
     - If admin: call `record.ApplyTombstone(context.SenderIdentityId, dto.DeletedAtUnixMs, isChannelAdmin: true)`.
     - Else: return `DomainResult.Failure(new DomainError("UNAUTHORIZED_ROLE", "Only the author or a channel administrator can delete this message."))`.
6. **Group Channel Write Aggregation**:
   - In `HandleMessageAsync`: Look up channel via `_channelRepo.GetDirectByIdAsync(context.ChannelId)` OR `_channelRepo.GetGroupByIdAsync(context.ChannelId)`.
   - Append `ChannelPayload` to whichever channel aggregate exists so group channel domain events fire.

#### Step 2.3: Security Test Suite
- In `Percolator.Apps.Chat.Tests/Ingress/ChatPayloadHandlerTests.cs`, implement explicit security test cases:
  - `HandleInboundAsync_ForgedReactionAuthor_ReturnsUnauthorizedAuthor`
  - `HandleInboundAsync_CrossChannelPayload_ReturnsChannelMismatch`
  - `HandleInboundAsync_ReplayedMessage_DoesNotOverwriteTombstone`
  - `HandleInboundAsync_ForgedInvitationSender_ReturnsUnauthorized`
  - `HandleInboundAsync_GroupAdminTombstone_WhenAdmin_SucceedsWithAdminPlaceholder`
  - `HandleInboundAsync_GroupAdminTombstone_WhenNonAdmin_ReturnsUnauthorizedRole`
  - `HandleInboundAsync_GroupMessage_AppendsToGroupChannelAggregate`

---

### Milestone 3: Outbound `ChatService` Alignment & Invariant Delegation

1. **Eliminate Rule Duplication in `ChatService`**:
   - In `EditMessageAsync`: Call `record.ApplyEdit(...)` directly; return entity's `DomainResult`.
   - In `DeleteMessageAsync`:
     - If author: call `record.ApplyTombstone(command.RequestedBy, deletedAtUnixMs, isChannelAdmin: false)`.
     - Else: verify if admin in `GroupChannel` and pass `isChannelAdmin: true`.
     - Returns entity's `DomainResult`.
   - In `AddReactionAsync` / `RemoveReactionAsync`: Call `record.AddReaction(...)` / `record.RemoveReaction(...)`.
2. **Group Channel Outbound Support**:
   - In `SendMessageAsync`: Check `_channelRepo.GetDirectByIdAsync(command.ChannelId)` OR `_channelRepo.GetGroupByIdAsync(command.ChannelId)`. Append `ChannelPayload` to whichever aggregate exists.
3. **Unit Tests**:
   - Update `ChatServiceTests.cs` in `Percolator.Apps.Chat.Tests/Features/Messaging/`.

---

### Milestone 4: Verification Checkpoints & Solution Gate

1. **Unit Test Run**:
   - Execute `dotnet test Percolator.Apps.Chat.Tests`.
   - Must pass 100% with zero failures.
2. **Whole Solution Regression Run**:
   - Execute `dotnet test Percolator.sln`.
   - All 270+ domain, application, plugin, and crypto tests must pass 100%.
3. **Compiler & Analyzer Check**:
   - Execute `dotnet build Percolator.sln`.
   - 0 errors, 0 new warnings.
4. **Git Verification**:
   - `git status` verifies all files are clean, properly located under `Features/`, `Ingress/`, and zero changes were made in `Percolator.Domain`.

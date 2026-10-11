# Percolator.Apps.Chat Implementation Plan

> [!NOTE]
> This is an ephemeral, step-by-step implementation plan executing the directives from [spec.md](spec.md) and [unit-testing.md](../unit-testing.md). It will be deleted after implementation is complete and verified.

---

## 1. Testing Standards & Execution Discipline

Per our testing directive ([unit-testing.md](../unit-testing.md)), all code in `Percolator.Apps.Chat` will be implemented strictly via **Red-Green-Refactor**:

### 1.1 High-Value Invariant Testing (AI Token Economy)
- **\u274c BANNED (Zero Value / Wasted Tokens)**:
  - Constructor field assignments (`new ChatMessageDto(...).Content == "..."`).
  - Auto-properties, getters, setters.
  - C# record structural equality or enum value checks.
  - Mock framework wiring checks (`mock.Setup(x => x.Foo()).Returns(1); mock.Object.Foo().Should().Be(1)`).
- **\u2705 MANDATORY (High-Value Defect Prevention)**:
  - **Domain Invariants**: Stepped padding boundary calculations; UTF-8 byte span indexing for mentions; 24-hour edit windows.
  - **State Transitions**: `PendingGroupInvitation` transitions (`Pending` $\rightarrow$ `Accepted`, `Pending` $\rightarrow$ `Declined`, `Pending` $\rightarrow$ `Expired`).
  - **Result & Error Codes**: Asserting specific `DomainError` codes (`EDIT_WINDOW_EXPIRED`, `UNAUTHORIZED_AUTHOR`, `INVALID_CONTENT`).
  - **Side-Effect & Delivery Assertions**: `IPayloadSender` dispatching padded envelopes; `IChatMessageRepository` mutations.
  - **Temporal Expirations**: Virtualized time validation via `FakeDateTimeProvider` (no wall-clock dependencies).

### 1.2 Anti-Bloat Guidelines
- **Rule A (Parameterized Tests)**: Use NUnit `[TestCase]` to collapse boundary checks (null, empty, whitespace content; padding boundary lengths).
- **Rule B (Assert the Delta)**: Verify only the result or state delta changed by the `Act` step.
- **Rule C (Object Mother Factory)**: Use `ChatTestFactory` for repeatable DTO/context instantiation.

---

## 2. Project Structure & Namespaces

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

## 3. Milestone 1: Models, DTOs & Invariant Logic (TDD)

### Step 1.1: RED — Write Invariant Tests
Create `Percolator.Apps.Chat.Tests/Models/SteppedPaddingTests.cs` and `MentionSpanTests.cs`:
- `SteppedPaddingTests.CalculatePaddedLength_ReturnsExpectedBucket`:
  ```csharp
  [TestCase(0, 256)]
  [TestCase(1, 256)]
  [TestCase(256, 256)]
  [TestCase(257, 512)]
  [TestCase(512, 512)]
  [TestCase(513, 1024)]
  [TestCase(1024, 1024)]
  [TestCase(1025, 2048)]
  [TestCase(2048, 2048)]
  [TestCase(2049, 4096)]
  [TestCase(4096, 4096)]
  [TestCase(4097, 8192)]
  public void CalculatePaddedLength_ReturnsExpectedBucket(int unpaddedLength, int expectedPaddedLength)
  {
      SteppedPadding.CalculatePaddedLength(unpaddedLength).Should().Be(expectedPaddedLength);
  }
  ```
- `MentionSpanTests.ValidateSpan_WithEmojiAndMultibyteText_PreservesUtf8ByteBoundaries`:
  Asserts that `MentionDto` offset calculations correctly account for multi-byte UTF-8 bytes (`Encoding.UTF8.GetByteCount`) when emojis/surrogates precede the mention.
- Run `dotnet test Percolator.Apps.Chat.Tests` $\rightarrow$ **Fails (RED)**.

### Step 1.2: GREEN — Implement Invariants & DTOs
- Implement `SteppedPadding.cs` in `Percolator.Apps.Chat.Services`.
- Implement `ChatPayloadType.cs`, `ReactionAction.cs`, `SystemNoticeType.cs`, `GroupInvitationStatus.cs`.
- Implement `ChatPayloadEnvelopeDto.cs`, `MentionDto.cs`, `ReplyContextDto.cs`, `LinkPreviewDto.cs`, `MediaAttachmentDto.cs`.
- Implement `ChatMessageDto.cs`, `MessageEditDto.cs`, `MessageTombstoneDto.cs`, `ReactionUpdateDto.cs`, `ReadReceiptDto.cs`, `SystemNoticeDto.cs`, `GroupInvitationDto.cs`, `GroupInvitationResponseDto.cs`.
- Implement internal persistence records: `ChatMessageRecord.cs`, `UnknownGroupMessageRecord.cs`, `PendingGroupInvitation.cs`.
- Implement read models: `ChatMessageReadModel.cs`, `ConversationSummaryReadModel.cs`, `ReactionGroupReadModel.cs`, `GroupInvitationSummaryReadModel.cs`.

### Step 1.3: CHECKPOINT 1
- Run `dotnet test Percolator.Apps.Chat.Tests`.
- **Criteria**: All tests pass (GREEN).
- Run `dotnet build Percolator.sln`.
- **Criteria**: 0 errors, 0 warnings.

---

## 4. Milestone 2: Ports & In-Memory Test Doubles

### Step 2.1: Implement Ports in `Percolator.Apps.Chat.Ports`
- `IChatService.cs`: Outbound commands port.
- `IChatMessageRepository.cs`: Rich message persistence port (insert, edit, tombstone, reaction, receipt, purge).
- `IGroupInvitationRepository.cs`: Group invitation persistence port.
- `IUnknownGroupMessageCacheRepository.cs`: Out-of-order group frames port.
- `IChatMessageQueryService.cs`, `IConversationListQueryService.cs`, `IGroupInvitationQueryService.cs`: Read query ports.

### Step 2.2: Implement Test Doubles in `Percolator.Apps.Chat.Tests/TestDoubles`
- `FakeDateTimeProvider`: Time virtualizer implementing `IDateTimeProvider` with `SetTime(DateTimeOffset)` and `Advance(TimeSpan)`.
- `InMemoryChatMessageRepository`: Dictionary-backed `IChatMessageRepository`.
- `InMemoryGroupInvitationRepository`: Dictionary-backed `IGroupInvitationRepository`.
- `TestPayloadSender`: Captures `OutboundPayloadContext` for verification without network I/O.
- `TestPayloadSerializer`: Deterministic JSON/Memory serializer implementing `IPayloadSerializer`.
- `ChatTestFactory`: Object Mother providing pre-built valid `DirectChannel`, `GroupChannel`, `SendMessageCommand`, etc.

### Step 2.3: CHECKPOINT 2
- Run `dotnet build Percolator.sln`.
- **Criteria**: 0 errors, 0 warnings. Clean compilation across all projects.

---

## 5. Milestone 3: Services & Handlers (TDD Cycle)

### Step 3.1: `ChatService` (Outbound Write Path)
- **Phase 1: RED**:
  Create `Percolator.Apps.Chat.Tests/Services/ChatServiceTests.cs`.
  Write focused, high-value invariant tests:
  1. `SendMessageAsync_ValidText_AppendsChannelPayload_AndDispatchesPaddedEnvelope`
  2. `SendMessageAsync_ClampsTimestampToUnixMilliseconds` (verifies sub-millisecond precision is stripped).
  3. `SendMessageAsync_ReplyContext_ExcludesSnippetFromWire` (verifies only parent ID and author ID are included).
  4. `SendMessageAsync_WithInvalidOrEmptyContent_ReturnsValidationError` (using `[TestCase(null)]`, `[TestCase("")]`, `[TestCase("   ")]`).
  5. `EditMessageAsync_Within24HourWindow_UpdatesRepository_AndDispatchesEditDto`
  6. `EditMessageAsync_Past24HourWindow_ReturnsEditWindowExpiredError`
  7. `EditMessageAsync_ByNonAuthor_ReturnsUnauthorizedError`
  8. `DeleteMessageAsync_Author_MarksTombstone_AndDispatchesTombstoneDto`
  9. `AddReactionAsync_RepeatedCalls_AreIdempotent`
  10. `MarkAsReadAsync_AdvancesCursorAndDispatchesReadReceiptDto`
  Run tests $\rightarrow$ **Fails (RED)**.
- **Phase 2: GREEN**:
  Implement `ChatService.cs` in `Percolator.Apps.Chat.Services`.
  Run tests $\rightarrow$ **Passes (GREEN)**.
- **Phase 3: REFACTOR**: Clean up allocations, verify no wall-clock dependencies remain.

### Step 3.2: `ChatPayloadHandler` (Inbound Write Path)
- **Phase 1: RED**:
  Create `Percolator.Apps.Chat.Tests/Handlers/ChatPayloadHandlerTests.cs`.
  Write focused invariant tests:
  1. `HandleInboundAsync_TextMessage_AppendsToChannel_AndPersistsToChatMessageRepository`
  2. `HandleInboundAsync_DiscardsSteppedPaddingCleanly` (asserts message text is intact regardless of padding bytes).
  3. `HandleInboundAsync_WithMentions_ValidatesUtf8ByteBoundaries`
  4. `HandleInboundAsync_EmojiReaction_AppliesReactionToChatRepo`
  5. `HandleInboundAsync_MessageEdit_WithinWindow_UpdatesChatRepo`
  6. `HandleInboundAsync_MessageEdit_NonAuthor_RejectsEdit`
  7. `HandleInboundAsync_MessageTombstone_ReplacesWithDeletedPlaceholder`
  8. `HandleInboundAsync_CorruptedEnvelope_ReturnsDeserializationError`
  9. `HandleInboundAsync_FutureUnknownType_ReturnsSuccessWithoutError` (forward compatibility).
  Run tests $\rightarrow$ **Fails (RED)**.
- **Phase 2: GREEN**:
  Implement `ChatPayloadHandler.cs` in `Percolator.Apps.Chat.Handlers`.
  Run tests $\rightarrow$ **Passes (GREEN)**.

### Step 3.3: `GroupInvitationService` (Consent State Machine)
- **Phase 1: RED**:
  Create `Percolator.Apps.Chat.Tests/Services/GroupInvitationServiceTests.cs`.
  Assert state transitions:
  1. `ReceiveInvitation_CreatesPendingRecord`
  2. `AcceptInvitation_TransitionsToAccepted_JoinsGroupChannel_AndDispatchesResponse`
  3. `DeclineInvitation_TransitionsToDeclined_AndDispatchesDeclineResponse`
  4. `AcceptInvitation_WhenExpired_ReturnsInvitationExpiredError`
  Run tests $\rightarrow$ **Fails (RED)**.
- **Phase 2: GREEN**:
  Implement `GroupInvitationService.cs` in `Percolator.Apps.Chat.Services`.
  Run tests $\rightarrow$ **Passes (GREEN)**.

### Step 3.4: `ChatPlugin` & Dependency Injection
- Implement `ChatPlugin.cs` (`IAppPlugin`).
- Implement `DependencyInjection/ServiceCollectionExtensions.cs` (`services.AddChatPlugin()`).

---

## 6. Milestone 4: Verification Checkpoints & Solution Gate

Execute this strict sequence before considering the task complete:

1. **Unit Test Run**:
   `dotnet test Percolator.Apps.Chat.Tests/Percolator.Apps.Chat.Tests.csproj`
   - Must pass 100% with zero failures.
2. **Whole Solution Regression Run**:
   `dotnet test Percolator.sln`
   - All 400+ domain, application, plugin, and crypto tests must pass 100%.
3. **Compiler & Analyzer Check**:
   `dotnet build Percolator.sln`
   - 0 errors, 0 new warnings.
4. **Git Verification & Commit**:
   - Check `git status` to ensure all new files are tracked and clean.
   - Commit changes cleanly: `git commit -m "feat(apps.chat): implement chat plugin models, ports, services, and unit tests"`.

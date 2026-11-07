# Percolator.Apps.Chat Delivery and Read Receipts Implementation Plan

## Refinement Status Gate
- [x] Lens 1: Scope & Feature Slice Framing (Status: Approved)
- [x] Lens 2: Onion Architecture & Primitive Obsession (Status: Approved)
- [x] Lens 3: Threat Modeling & Security Posture (Status: Approved)
- [x] Lens 4: High-Value, Low-Brittle Unit Test Design (Status: Approved)
- [x] Lens 5: Cross-Layer Infrastructure Alignment (Status: Approved)
- [x] Implementation Gate: UNLOCKED

---

## 1. Scope & Architectural Boundaries

### 1.1 In-Scope Capabilities
- **Signal-Parity Three-Stage Receipt Lifecycle**:
  - `Sent` (`✓` single tick, neutral/grey): The message has been encrypted via the Double Ratchet sending chain and persisted to the local transport outbox (`OutboxJob`).
  - `Delivered` (`✓✓` double tick, neutral/grey): The recipient node has unwrapped the envelope, stepped the Double Ratchet receiving chain, committed the `ChatMessage` to local storage, and returned an automated end-to-end encrypted `DeliveryReceiptDto` (`ChatPayloadType.DeliveryReceipt = 10`).
  - `Read` (`✓✓` double tick, accent/blue): The recipient user/persona has actively viewed the conversation while `SendReadReceipts` is enabled, dispatching an end-to-end encrypted `ReadReceiptDto` (`ChatPayloadType.ReadReceipt = 5`). Reading a message implicitly confirms delivery (`DeliveredAtUnixMs ??= ReadAtUnixMs`).
- **Wire Protocol & Serialization Contracts**:
  - Add `ChatPayloadType.DeliveryReceipt = 10` to `Percolator.Apps.Chat.Features.Messaging.ChatPayloadType`.
  - Introduce `DeliveryReceiptDto(ChannelId ChannelId, PublicIdentityId DelivererId, PayloadId UpToPayloadId, ulong DeliveredAtUnixMs)` in `Percolator.Apps.Chat/Features/Messaging/DeliveryReceiptDto.cs`.
  - Add `DeliveryReceiptProto` to `Percolator.Infrastructure/Protos/chat.proto` and bi-directional serialization in `ProtobufPayloadSerializer.cs`.
- **Automated Ingress Delivery Receipt Generation & Disappearing Timer Semantics**:
  - In `ChatPayloadHandler.HandleMessageAsync`, when a `DirectChannel` (1:1 chat) message is committed to `IChatMessageRepository`, automatically construct and enqueue a `DeliveryReceiptDto` to `IPayloadSender` (`OutboundEgressPipeline`) if `_payloadSender` is provided.
  - Resolve the return `DeliveryRoute` (`DirectP2P` vs `RelayedOneToOne`) by checking `_peerMailboxRoutingRepo?.GetBestRouteAsync(dto.AuthorId, context.ReceivedAtUtc, ct)`.
  - Resolve the local deliverer identity (`PublicIdentityId`) from `directChannel.OwnerIdentityId != dto.AuthorId ? directChannel.OwnerIdentityId : directChannel.RemotePeerId`.
  - In `ChatPayloadHandler.HandleDeliveryReceiptAsync`, validate `dto.DelivererId == context.SenderIdentityId` and `dto.ChannelId == context.ChannelId`, then invoke `MarkAsDelivered(effectiveDeliveredMs)` on the target message and all preceding undelivered messages in that channel authored by the local user (`msg.AuthorId != dto.DelivererId`).
  - Preserve disappearing-message timer semantics: `MarkAsDelivered` sets `DeliveredAtUnixMs` only and **never** starts or alters `ExpiresAtUnixMs` (which begins strictly on `MarkAsRead`).
- **User-Driven & Simulator Read Receipt Orchestration**:
  - Expose `MarkConversationAsReadAsync(SimulatedNodeId readerNodeId, SimulatedNodeId senderNodeId, Guid upToMessageId, CancellationToken ct = default)` on `DirectChatCoordinator`.
  - In `PersonaCardViewModel.ExecuteRefreshChatTranscript`, when the transcript contains incoming unread messages (`!m.IsOutgoing && !m.IsRead`) and `SendReadReceipts == true` and `IsListening == true`, invoke `MarkConversationAsReadAsync` for the latest unread incoming message.
  - Ensure `SimulatorIngressDispatchingFilterService`, `SimulatedPersonaHost.DrainMailboxAsync`, and `DirectChatCoordinator` flush any newly enqueued outbox jobs (delivery receipts and read receipts) so return receipts traverse the loopback transport immediately.
- **Read-Model, Persistence & UI Presentation**:
  - Extend `ChatMessage`, `ChatMessageEntity`, `PercolatorDbContext`, `SqlChatMessageRepository`, `ChatMessageReadModel`, `SqlChatMessageQueryService`, `InMemoryChatMessageQueryService`, `SimulatorChatMessageReadModel`, and `SimulatorChatQueryService` with `ulong? DeliveredAtUnixMs` (and `ulong? ReadAtUnixMs` on `ChatMessageReadModel`).
  - Update `PersonaCardView.axaml` and `ChatMessageItemViewModel` so the message body and receipt tick are rendered in separate `TextBlock` elements, styling the `✓✓` tick with `#38BDF8` (Signal sky-blue) when `IsRead == true` and secondary grey (`#888888`) when `IsDelivered && !IsRead` or `Sent`.

### 1.2 Explicit Non-Goals
- **No Online/Offline Presence Beacons**: Nodes never emit heartbeat pings, typing indicators, or online status broadcasts.
- **No Unencrypted Transport ACKs as Delivery Ticks**: gRPC transport-level `DeliveryAck` only marks the sender's `OutboxJob` as `OutboxStatus.Delivered` (meaning handed off to socket or relay mailbox). Visual `✓✓` message delivery ticks require an end-to-end encrypted `DeliveryReceiptDto` signed/ratcheted by the recipient identity.
- **No Group Chat Delivery Receipt Fan-Out**: Multi-party `GroupChannel` messages do not emit automated `DeliveryReceiptDto` envelopes to avoid $O(N)$ traffic amplification.
- **No Core Layer Pollution for Simulator Queries**: Simulator-specific read models (`SimulatorChatMessageReadModel`) remain strictly inside `Percolator.Simulator`.

---

## 2. Step-by-Step Implementation Work Packages (Context-Sized Slices)

To enable a fast, context-bounded AI model (e.g., Gemini 3.8 Flash) to implement this feature without context overflow or cross-layer compilation deadlocks, the implementation is decomposed into **6 sequential, self-contained Work Packages (WP-1 through WP-6)**. Each work package modifies 2–6 files and compiles cleanly before advancing to the next.

---

### WP-1: Chat Domain Models, Wire Contracts & Domain Unit Tests
**Context Scope**: `Percolator.Apps.Chat/Features/Messaging/` and `Percolator.Apps.Chat.Tests/Features/Messaging/` (6 files)

#### 1. Wire Enum (`Percolator.Apps.Chat/Features/Messaging/ChatPayloadType.cs`)
Add `DeliveryReceipt = 10`:
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
    GroupInvitationResponse = 8,
    ProfileCapability = 9,
    DeliveryReceipt = 10
}
```

#### 2. Wire DTO (`Percolator.Apps.Chat/Features/Messaging/DeliveryReceiptDto.cs`) [NEW FILE]
```csharp
using Percolator.Domain.Channels.ValueObjects;
using Percolator.Domain.Identities.ValueObjects;

namespace Percolator.Apps.Chat.Features.Messaging;

public sealed record DeliveryReceiptDto(
    ChannelId ChannelId,
    PublicIdentityId DelivererId,
    PayloadId UpToPayloadId,
    ulong DeliveredAtUnixMs);
```

#### 3. Domain Aggregate (`Percolator.Apps.Chat/Features/Messaging/ChatMessage.cs`)
- Add property:
  ```csharp
  public ulong? DeliveredAtUnixMs { get; private set; }
  ```
- Append optional parameter `ulong? deliveredAtUnixMs = null` to the end of the constructor (preserving positional source compatibility for all 39 existing callers):
  ```csharp
  public ChatMessage(
      PayloadId id,
      ChannelId channelId,
      PublicIdentityId authorId,
      ulong timestampUnixMs,
      string content,
      IReadOnlyList<MentionDto>? mentions = null,
      ReplyContextDto? replyContext = null,
      LinkPreviewDto? linkPreview = null,
      IReadOnlyList<MediaAttachmentDto>? attachments = null,
      int? expireAfterSeconds = null,
      ulong? expiresAtUnixMs = null,
      ulong? editedAtUnixMs = null,
      bool isTombstone = false,
      ulong? deletedAtUnixMs = null,
      PublicIdentityId? deletedBy = null,
      IEnumerable<ChatReaction>? reactions = null,
      ulong? readAtUnixMs = null,
      ulong? deliveredAtUnixMs = null)
  ```
  Inside constructor:
  ```csharp
  ReadAtUnixMs = readAtUnixMs;
  DeliveredAtUnixMs = deliveredAtUnixMs ?? readAtUnixMs;
  ```
- Add `MarkAsDelivered` and update `MarkAsRead`:
  ```csharp
  /// <summary>
  /// Marks the message as delivered to the recipient device at the given timestamp.
  /// Does not start the disappearing-message expiration timer.
  /// </summary>
  public void MarkAsDelivered(ulong deliveredAtUnixMs)
  {
      DeliveredAtUnixMs ??= deliveredAtUnixMs;
  }

  /// <summary>
  /// Marks the message as read at the given timestamp, implicitly ensuring delivery is recorded,
  /// and starts the disappearing-message countdown for 1:1 messages that defer expiration until read.
  /// </summary>
  public void MarkAsRead(ulong readAtUnixMs)
  {
      DeliveredAtUnixMs ??= readAtUnixMs;
      ReadAtUnixMs ??= readAtUnixMs;

      if (ExpireAfterSeconds is > 0)
      {
          ulong readBasedExpiry = ReadAtUnixMs.Value + ((ulong)ExpireAfterSeconds.Value * 1000UL);
          ExpiresAtUnixMs = ExpiresAtUnixMs.HasValue
              ? Math.Min(ExpiresAtUnixMs.Value, readBasedExpiry)
              : readBasedExpiry;
      }
  }
  ```

#### 4. Read Model (`Percolator.Apps.Chat/Features/Messaging/ChatMessageReadModel.cs`)
Extend with optional default parameters at the end to preserve positional construction compatibility:
```csharp
public sealed record ChatMessageReadModel(
    PayloadId Id,
    ChannelId ChannelId,
    PublicIdentityId AuthorId,
    ulong TimestampUnixMs,
    string Content,
    IReadOnlyList<MentionDto> Mentions,
    ReplyContextDto? ReplyContext,
    LinkPreviewDto? LinkPreview,
    IReadOnlyList<MediaAttachmentDto> Attachments,
    ulong? EditedAtUnixMs,
    bool IsTombstone,
    IReadOnlyList<ReactionGroupReadModel> Reactions,
    ulong? DeliveredAtUnixMs = null,
    ulong? ReadAtUnixMs = null);
```

#### 5. Application Service (`Percolator.Apps.Chat/Features/Messaging/ChatService.cs`)
In `MarkAsReadAsync(MarkAsReadCommand command, CancellationToken ct = default)` (lines 380–403), ensure only messages authored by the remote peer (`AuthorId != command.ReaderId`) are marked read locally:
```csharp
var targetMessage = await _chatRepo.GetByIdAsync(command.UpToPayloadId, ct).ConfigureAwait(false);
if (targetMessage is not null && targetMessage.ChannelId == command.ChannelId)
{
    if (targetMessage.AuthorId != command.ReaderId)
    {
        targetMessage.MarkAsRead(nowMs);
        await _chatRepo.UpdateAsync(targetMessage, ct).ConfigureAwait(false);
    }

    var channelMessages = await _chatRepo.GetByChannelIdAsync(command.ChannelId, ct).ConfigureAwait(false);
    foreach (var msg in channelMessages)
    {
        if (msg.Id != targetMessage.Id &&
            msg.AuthorId != command.ReaderId &&
            msg.TimestampUnixMs <= targetMessage.TimestampUnixMs &&
            msg.ReadAtUnixMs is null)
        {
            msg.MarkAsRead(nowMs);
            await _chatRepo.UpdateAsync(msg, ct).ConfigureAwait(false);
        }
    }
}
```

#### 6. Domain Unit Tests (`Percolator.Apps.Chat.Tests/Features/Messaging/ChatMessageTests.cs`)
Add unit tests:
- `MarkAsDelivered_SetsDeliveredAtUnixMs_Idempotently_WithoutStartingExpirationTimer`
- `MarkAsRead_SetsBothReadAndDeliveredAtUnixMs_AndStartsExpirationTimer_WhenExpireAfterSecondsConfigured`

> [!CAUTION]
> **Anti-Shortcut Guidelines for WP-1**:
> - **Do NOT reorder constructor parameters** on `ChatMessage` or `ChatMessageReadModel`. `ulong? deliveredAtUnixMs = null` MUST be the very last parameter on `ChatMessage` (after `readAtUnixMs`), and `DeliveredAtUnixMs = null, ReadAtUnixMs = null` MUST be at the end of `ChatMessageReadModel` so all 39+ existing positional callers compile without modification.
> - **Do NOT touch `ExpiresAtUnixMs` inside `MarkAsDelivered`**. Disappearing message timers only start when the message is **read** (`MarkAsRead`), never on delivery.
> - **Do NOT use `=` instead of `??=`** in `MarkAsDelivered` or `MarkAsRead`. Both timestamps are immutable once set (`DeliveredAtUnixMs ??= ...`, `ReadAtUnixMs ??= ...`) so duplicate receipts cannot overwrite the original timestamp.

**WP-1 Verification Command**:
```powershell
dotnet test Percolator.Apps.Chat.Tests/Percolator.Apps.Chat.Tests.csproj --filter "FullyQualifiedName~ChatMessageTests"
```

---

### WP-2: Ingress Demuxing, Automated Delivery Ack & Ingress Unit Tests
**Context Scope**: `Percolator.Apps.Chat/Ingress/ChatPayloadHandler.cs` and `Percolator.Apps.Chat.Tests/Ingress/ChatPayloadHandlerTests.cs` (2 files)

#### 1. Constructor Injection (`Percolator.Apps.Chat/Ingress/ChatPayloadHandler.cs`)
Add optional `IPayloadSender? payloadSender = null` as the final parameter:
```csharp
private readonly IPayloadSender? _payloadSender;

public ChatPayloadHandler(
    IChatMessageRepository chatRepo,
    IChannelRepository channelRepo,
    IGroupInvitationRepository invitationRepo,
    IPayloadSerializer serializer,
    IDateTimeProvider timeProvider,
    IUnknownGroupMessageCacheRepository? unknownGroupCache = null,
    IBlobStorageService? blobStorage = null,
    IPeerProfileRepository? peerProfileRepo = null,
    IProfileDeliveryClient? profileDeliveryClient = null,
    IProfileManager? profileManager = null,
    ICryptoEngine? cryptoEngine = null,
    IPeerMailboxRoutingRepository? peerMailboxRoutingRepo = null,
    IPayloadSender? payloadSender = null)
```
Assign `_payloadSender = payloadSender;` in the constructor body.
*Note*: In `SimulatedPersonaHost.cs` and production DI (`ServiceCollectionExtensions.AddChatApp()`), `IPayloadSender` is registered in the container and will be automatically injected by `Microsoft.Extensions.DependencyInjection`. In `TestIdentityNode.cs`, `ChatPayloadHandler` is instantiated manually without `payloadSender` (defaulting to `null`), keeping existing `DirectChatScenarioTests` and `RelayedChatScenarioTests` ratchet step counts unchanged unless explicitly tested.

#### 2. Switch Dispatch in `HandleInboundAsync` (`Percolator.Apps.Chat/Ingress/ChatPayloadHandler.cs`)
Add case to the `envelope.Type switch`:
```csharp
ChatPayloadType.DeliveryReceipt => await HandleDeliveryReceiptAsync(envelope.Data, context, ct).ConfigureAwait(false),
```

#### 3. Automated Delivery Receipt Emission in `HandleMessageAsync` (`Percolator.Apps.Chat/Ingress/ChatPayloadHandler.cs`)
Immediately after `await _chatRepo.AddAsync(message, ct).ConfigureAwait(false);` (line 315), add:
```csharp
if (directChannel is not null && _payloadSender is not null)
{
    var delivererId = directChannel.OwnerIdentityId != dto.AuthorId
        ? directChannel.OwnerIdentityId
        : directChannel.RemotePeerId;

    var route = DeliveryRoute.DirectP2P;
    if (_peerMailboxRoutingRepo is not null)
    {
        var bestRoute = await _peerMailboxRoutingRepo
            .GetBestRouteAsync(dto.AuthorId, context.ReceivedAtUtc, ct)
            .ConfigureAwait(false);
        if (bestRoute is not null)
        {
            route = DeliveryRoute.RelayedOneToOne;
        }
    }

    var receiptDto = new DeliveryReceiptDto(
        context.ChannelId,
        delivererId,
        dto.Id,
        receiptBaselineMs);

    var envelopeBytes = SteppedPadding.CreatePaddedEnvelopeBytes(
        _serializer,
        ChatPayloadType.DeliveryReceipt,
        receiptDto);

    var outboundContext = new OutboundPayloadContext(
        context.ChannelId,
        delivererId,
        dto.AuthorId,
        context.SenderDeviceId,
        AppId.Chat,
        envelopeBytes,
        route);

    // Best-effort enqueue: receipt emission failure must not roll back message storage
    await _payloadSender.SendPayloadAsync(outboundContext, ct).ConfigureAwait(false);
}
```

#### 4. Inbound `HandleDeliveryReceiptAsync` (`Percolator.Apps.Chat/Ingress/ChatPayloadHandler.cs`)
Add method:
```csharp
private async ValueTask<DomainResult> HandleDeliveryReceiptAsync(
    ReadOnlyMemory<byte> data,
    InboundPayloadContext context,
    CancellationToken ct)
{
    var receiptResult = _serializer.Deserialize<DeliveryReceiptDto>(data);
    if (receiptResult.IsFailure)
    {
        return DomainResult.Failure(receiptResult.Error);
    }

    var dto = receiptResult.Value;
    if (dto.DelivererId != context.SenderIdentityId)
    {
        return DomainResult.Failure(new DomainError(
            "UNAUTHORIZED_AUTHOR",
            "Delivery receipt deliverer does not match cryptographic sender."));
    }

    if (dto.ChannelId != context.ChannelId)
    {
        return DomainResult.Failure(new DomainError(
            "CHANNEL_MISMATCH",
            "Delivery receipt belongs to a different channel."));
    }

    ulong effectiveDeliveredMs = (ulong)context.ReceivedAtUtc.ToUnixTimeMilliseconds();
    var targetMessage = await _chatRepo.GetByIdAsync(dto.UpToPayloadId, ct).ConfigureAwait(false);
    if (targetMessage is not null && targetMessage.ChannelId == context.ChannelId)
    {
        if (targetMessage.AuthorId != dto.DelivererId)
        {
            targetMessage.MarkAsDelivered(effectiveDeliveredMs);
            await _chatRepo.UpdateAsync(targetMessage, ct).ConfigureAwait(false);
        }

        var channelMessages = await _chatRepo.GetByChannelIdAsync(context.ChannelId, ct).ConfigureAwait(false);
        foreach (var msg in channelMessages)
        {
            if (msg.Id != targetMessage.Id &&
                msg.AuthorId != dto.DelivererId &&
                msg.TimestampUnixMs <= targetMessage.TimestampUnixMs &&
                msg.DeliveredAtUnixMs is null)
            {
                msg.MarkAsDelivered(effectiveDeliveredMs);
                await _chatRepo.UpdateAsync(msg, ct).ConfigureAwait(false);
            }
        }
    }

    return DomainResult.Success();
}
```

#### 5. Inbound `HandleReadReceiptAsync` Directionality Guard (`Percolator.Apps.Chat/Ingress/ChatPayloadHandler.cs`)
Update lines 522–539 in `HandleReadReceiptAsync` so a read receipt from peer B only marks peer A's outgoing messages (`AuthorId != dto.ReaderId`):
```csharp
var targetMessage = await _chatRepo.GetByIdAsync(dto.UpToPayloadId, ct).ConfigureAwait(false);
if (targetMessage is not null && targetMessage.ChannelId == context.ChannelId)
{
    if (targetMessage.AuthorId != dto.ReaderId)
    {
        targetMessage.MarkAsRead(effectiveReadMs);
        await _chatRepo.UpdateAsync(targetMessage, ct).ConfigureAwait(false);
    }

    var channelMessages = await _chatRepo.GetByChannelIdAsync(context.ChannelId, ct).ConfigureAwait(false);
    foreach (var msg in channelMessages)
    {
        if (msg.Id != targetMessage.Id &&
            msg.AuthorId != dto.ReaderId &&
            msg.TimestampUnixMs <= targetMessage.TimestampUnixMs &&
            msg.ReadAtUnixMs is null)
        {
            msg.MarkAsRead(effectiveReadMs);
            await _chatRepo.UpdateAsync(msg, ct).ConfigureAwait(false);
        }
    }
}
```

#### 6. Ingress Unit Tests (`Percolator.Apps.Chat.Tests/Ingress/ChatPayloadHandlerTests.cs`)
Add unit tests using the existing `TestPayloadSender` and `InMemoryChatMessageRepository`:
- `HandleInboundAsync_WhenMessageReceivedOnDirectChannel_WithPayloadSender_EnqueuesDeliveryReceipt`
- `HandleInboundAsync_WhenDeliveryReceiptReceived_WithValidSender_MarksTargetAndPrecedingMessagesDelivered`
- `HandleInboundAsync_WhenReadReceiptReceived_WithValidSender_MarksTargetAndPrecedingMessagesReadAndDelivered`
- `HandleInboundAsync_WhenDeliveryReceiptReceived_WithSpoofedSender_ReturnsUnauthorizedAuthorAndDoesNotMutate`
- `HandleInboundAsync_WhenDeliveryReceiptReceived_WithMismatchedChannel_ReturnsChannelMismatchAndDoesNotMutate`

> [!CAUTION]
> **Anti-Shortcut Guidelines for WP-2**:
> - **Do NOT inject `TestPayloadSender` into the shared `[SetUp]` `_sut` instance** in `ChatPayloadHandlerTests.cs` if any existing test asserts on shared state, or ensure `_payloadSender = new TestPayloadSender()` in `[SetUp]` does not break any existing test. (Even better: instantiate a `TestPayloadSender` field `_payloadSender = new TestPayloadSender()` in `[SetUp]` and pass it to `_sut`, then run all tests in `ChatPayloadHandlerTests` to verify 100% pass).
> - **Do NOT modify `TestIdentityNode.cs`** in `Percolator.ApplicationIntegrationTests` to pass `OutboundEgressPipeline` into `ChatPayloadHandler` by default—doing so will double the number of Double Ratchet envelopes in `DirectChatScenarioTests` and break existing sequence number assertions!
> - **Do NOT return failure from `HandleMessageAsync` if `_payloadSender.SendPayloadAsync` fails**. The incoming message is already persisted to `_chatRepo`; delivery receipt emission is best-effort and must not cause the inbound message to be rejected.
> - **Do NOT trust `dto.DeliveredAtUnixMs` or `dto.ReadAtUnixMs` from the wire** when mutating local message state. Always use `(ulong)context.ReceivedAtUtc.ToUnixTimeMilliseconds()` to prevent remote clock-skew manipulation.
> - **In `HandleInboundAsync_WhenMessageReceivedOnDirectChannel_WithPayloadSender_EnqueuesDeliveryReceipt`**, do not just assert `_payloadSender.SentPayloads.Should().HaveCount(1)`. Unwrap the outer `ChatPayloadEnvelopeDto` via `_serializer.Deserialize<ChatPayloadEnvelopeDto>(sent.Payload)`, assert `envelope.Type == ChatPayloadType.DeliveryReceipt`, and deserialize the inner `DeliveryReceiptDto` to verify `UpToPayloadId == msgId` and `DelivererId == channel.OwnerIdentityId`.

**WP-2 Verification Command**:
```powershell
dotnet test Percolator.Apps.Chat.Tests/Percolator.Apps.Chat.Tests.csproj
```

---

### WP-3: Protobuf Wire Schema, Serializer & Serialization Unit Tests
**Context Scope**: `Percolator.Infrastructure/Protos/chat.proto`, `Percolator.Infrastructure/Serialization/ProtobufPayloadSerializer.cs`, and `Percolator.Infrastructure.Tests/Plugins/Chat/ProtobufChatSerializerTests.cs` (3 files)

#### 1. Protobuf Schema (`Percolator.Infrastructure/Protos/chat.proto`)
Add `DeliveryReceiptProto` right after `ReadReceiptProto` (around line 90):
```protobuf
message DeliveryReceiptProto {
  bytes channel_id = 1;
  bytes deliverer_id = 2;
  bytes up_to_payload_id = 3;
  uint64 delivered_at_unix_ms = 4;
}
```

#### 2. Protobuf Serializer (`Percolator.Infrastructure/Serialization/ProtobufPayloadSerializer.cs`)
- In `Deserialize<T>` switch (around line 58):
  ```csharp
  Type t when t == typeof(DeliveryReceiptDto) => FromDeliveryReceiptProto(DeliveryReceiptProto.Parser.ParseFrom(data.Span)),
  ```
- In `ToProtoMessage<T>` switch (around line 89):
  ```csharp
  DeliveryReceiptDto dr => ToDeliveryReceiptProto(dr),
  ```
- Add static mapping helpers right after `FromReadReceiptProto` (around line 604):
  ```csharp
  private static DeliveryReceiptProto ToDeliveryReceiptProto(DeliveryReceiptDto dto) =>
      new()
      {
          ChannelId = ByteString.CopyFrom(dto.ChannelId.ToByteArray()),
          DelivererId = ByteString.CopyFrom(dto.DelivererId.ToByteArray()),
          UpToPayloadId = ByteString.CopyFrom(dto.UpToPayloadId.ToByteArray()),
          DeliveredAtUnixMs = dto.DeliveredAtUnixMs
      };

  private static DeliveryReceiptDto FromDeliveryReceiptProto(DeliveryReceiptProto proto) =>
      new(
          ChannelId.FromBytes(proto.ChannelId.Span),
          PublicIdentityId.FromBytes(proto.DelivererId.Span),
          PayloadId.FromBytes(proto.UpToPayloadId.Span),
          proto.DeliveredAtUnixMs);
  ```

#### 3. Serialization Unit Test (`Percolator.Infrastructure.Tests/Plugins/Chat/ProtobufChatSerializerTests.cs`)
Add round-trip test `DeliveryReceiptDto_RoundTripsThroughProtobufSerialization`.

> [!CAUTION]
> **Anti-Shortcut Guidelines for WP-3**:
> - **Do NOT hand-write or create a C# class for `DeliveryReceiptProto`**. It is auto-generated by `Grpc.Tools` during `dotnet build` from `Percolator.Infrastructure/Protos/chat.proto` into namespace `Percolator.Infrastructure2.Protos`.
> - **Do NOT forget to update BOTH `Deserialize<T>` AND `ToProtoMessage<T>`** in `ProtobufPayloadSerializer.cs`. Updating only one causes a runtime `NotSupportedException` during round-trip serialization.

**WP-3 Verification Command**:
```powershell
dotnet test Percolator.Infrastructure.Tests/Percolator.Infrastructure.Tests.csproj --filter "FullyQualifiedName~ProtobufChatSerializerTests"
```

---

### WP-4: SQLite Persistence, Query Adapters & Persistence Unit Tests
**Context Scope**: `Percolator.Infrastructure/Persistence/`, `Percolator.Infrastructure.Tests/Plugins/Chat/`, and `Percolator.ApplicationIntegrationTests/Adapters/Queries/` (6 files)

#### 1. SQLite Entity (`Percolator.Infrastructure/Persistence/Entities/ChatEntities.cs`)
In `ChatMessageEntity` (around line 30):
```csharp
public ulong? DeliveredAtUnixMs { get; set; }
public ulong? ReadAtUnixMs { get; set; }
```

#### 2. EF Core DbContext (`Percolator.Infrastructure/Persistence/PercolatorDbContext.cs`)
In `OnModelCreating` for `ChatMessageEntity` (around line 380):
```csharp
entity.Property(e => e.DeliveredAtUnixMs).HasConversion<long?>();
entity.Property(e => e.ReadAtUnixMs).HasConversion<long?>();
```

#### 3. SQLite Repository (`Percolator.Infrastructure/Persistence/Repositories/SqlChatMessageRepository.cs`)
- In `AddAsync` (around line 39):
  ```csharp
  DeliveredAtUnixMs = record.DeliveredAtUnixMs,
  ReadAtUnixMs = record.ReadAtUnixMs,
  ```
- In `UpdateAsync` (around line 227):
  ```csharp
  entity.DeliveredAtUnixMs = record.DeliveredAtUnixMs;
  entity.ReadAtUnixMs = record.ReadAtUnixMs;
  ```
- In `ToDomainModel` (around line 446):
  ```csharp
  chatReactions,
  entity.ReadAtUnixMs,
  entity.DeliveredAtUnixMs);
  ```

#### 4. SQLite Query Service (`Percolator.Infrastructure/Persistence/Repositories/SqlChatMessageQueryService.cs`)
In `GetMessagesAsync` (around lines 183–196), pass `entity.DeliveredAtUnixMs` and `entity.ReadAtUnixMs` to `ChatMessageReadModel`:
```csharp
result.Add(new ChatMessageReadModel(
    PayloadId.FromGuid(entity.Id),
    ChannelId.FromGuid(entity.ChannelId),
    PublicIdentityId.FromBytes(entity.AuthorId),
    entity.TimestampUnixMs,
    entity.Content,
    mentionDtos,
    replyContext,
    linkPreview,
    attachmentDtos,
    entity.EditedAtUnixMs,
    entity.IsTombstone,
    reactionGroups,
    entity.DeliveredAtUnixMs,
    entity.ReadAtUnixMs));
```

#### 5. In-Memory Query Adapter (`Percolator.ApplicationIntegrationTests/Adapters/Queries/InMemoryQueryAdapters.cs`)
In `InMemoryChatMessageQueryService.GetMessagesAsync` (lines 41–53), pass `m.DeliveredAtUnixMs` and `m.ReadAtUnixMs`:
```csharp
.Select(m => new ChatMessageReadModel(
    m.Id,
    m.ChannelId,
    m.AuthorId,
    m.TimestampUnixMs,
    m.Content,
    m.Mentions,
    m.ReplyContext,
    m.LinkPreview,
    m.Attachments,
    m.EditedAtUnixMs,
    m.IsTombstone,
    Array.Empty<ReactionGroupReadModel>(),
    m.DeliveredAtUnixMs,
    m.ReadAtUnixMs))
```

#### 6. SQLite Persistence Unit Test (`Percolator.Infrastructure.Tests/Plugins/Chat/SqlChatMessageRepositoryTests.cs`)
Add test `AddAsync_And_UpdateAsync_PersistAndRetrieve_DeliveredAtUnixMs`.

> [!CAUTION]
> **Anti-Shortcut Guidelines for WP-4**:
> - **Do NOT omit `.HasConversion<long?>()` in `PercolatorDbContext.cs`**. SQLite EF Core provider does not natively map nullable unsigned 64-bit integers (`ulong?`) without `HasConversion<long?>()`.
> - **Do NOT generate EF Core Migration files** (`dotnet ef migrations add`). This repository uses `EnsureCreatedAsync` / `IRelationalDatabaseCreator.CreateTablesAsync` directly from `PercolatorDbContext`.
> - **Update ALL THREE places in `SqlChatMessageRepository.cs`**: `AddAsync`, `UpdateAsync`, AND `ToDomainModel`. Missing `UpdateAsync` is a classic bug because `MarkAsDelivered` is called on existing messages via `UpdateAsync`.
> - **Mind the parameter order in `ToDomainModel` vs `ChatMessageReadModel`**:
>   - `new ChatMessage(...)` takes `(..., entity.ReadAtUnixMs, entity.DeliveredAtUnixMs)`.
>   - `new ChatMessageReadModel(...)` takes `(..., entity.DeliveredAtUnixMs, entity.ReadAtUnixMs)`.
>   Use named arguments (`readAtUnixMs: entity.ReadAtUnixMs, deliveredAtUnixMs: entity.DeliveredAtUnixMs`) at call sites to eliminate any risk of swapping the two `ulong?` arguments!

**WP-4 Verification Command**:
```powershell
dotnet test Percolator.Infrastructure.Tests/Percolator.Infrastructure.Tests.csproj --filter "FullyQualifiedName~SqlChatMessage"
```

---

### WP-5: Simulator Return-Receipt Outbox Flushing & Query Contracts
**Context Scope**: `Percolator.Simulator/Features/DirectChat/` and `Percolator.Simulator/Features/Topology/` (4 files)

#### 1. Simulator Query Contract (`Percolator.Simulator/Features/DirectChat/ISimulatorChatQueryService.cs`)
Add `ulong? DeliveredAtUnixMs` to `SimulatorChatMessageReadModel`:
```csharp
public sealed record SimulatorChatMessageReadModel(
    Guid MessageId,
    Guid ChannelId,
    PublicIdentityId AuthorId,
    string Content,
    ulong TimestampUnixMs,
    ulong? DeliveredAtUnixMs,
    ulong? ReadAtUnixMs,
    ulong? EditedAtUnixMs,
    IReadOnlyList<string> Reactions);
```

#### 2. Simulator Query Implementation (`Percolator.Simulator/Features/DirectChat/SimulatorChatQueryService.cs`)
In `GetTranscriptAsync`, project `m.DeliveredAtUnixMs` in the `.Select(...)` query (line 39) and pass `m.DeliveredAtUnixMs` to `new SimulatorChatMessageReadModel(...)` (line 69):
```csharp
.Select(m => new
{
    m.Id,
    m.ChannelId,
    m.AuthorId,
    m.Content,
    m.TimestampUnixMs,
    m.DeliveredAtUnixMs,
    m.ReadAtUnixMs,
    m.EditedAtUnixMs
})
```
And in the loop constructing `SimulatorChatMessageReadModel`:
```csharp
result.Add(new SimulatorChatMessageReadModel(
    m.Id,
    m.ChannelId,
    PublicIdentityId.FromBytes(m.AuthorId),
    m.Content,
    m.TimestampUnixMs,
    m.DeliveredAtUnixMs,
    m.ReadAtUnixMs,
    m.EditedAtUnixMs,
    msgReactions ?? (IReadOnlyList<string>)Array.Empty<string>()));
```

#### 3. Direct Ingress Return-Receipt Outbox Flush (`Percolator.Simulator/Features/Topology/SimulatorDirectIngressBridge.cs` & `SimulatedPersonaHost.cs`)
When a persona receives an inbound message via Kestrel direct ingress, `ChatPayloadHandler.HandleMessageAsync` enqueues a `DeliveryReceiptDto` `OutboxJob` into the recipient's `_outboxRepo`.
- In `SimulatorDirectIngressBridge.cs` (`SimulatorIngressDispatchingFilterService`):
  Add `using Percolator.Application2.Egress;` at the top of `SimulatorDirectIngressBridge.cs`, and add `OutboxWorker? outboxWorker = null` to the constructor parameters of `SimulatorIngressDispatchingFilterService`:
  ```csharp
  private readonly OutboxWorker? _outboxWorker;

  public SimulatorIngressDispatchingFilterService(
      IIngressFilterService inner,
      InboundEnvelopeContextHolder contextHolder,
      IInboundIngressPipeline ingressPipeline,
      ISimulatorLogSink logSink,
      string nodeDisplayName,
      OutboxWorker? outboxWorker = null)
  ```
  Assign `_outboxWorker = outboxWorker;` in the constructor.
  In `CheckIngressAllowedAsync`, after `var ingressResult = await _ingressPipeline.ProcessInboundAsync(envelope, ct);` succeeds (line 110), flush any newly enqueued return receipts:
  ```csharp
  if (_outboxWorker is not null)
  {
      await _outboxWorker.ProcessBatchAsync(50, ct);
  }
  ```
- In `SimulatedPersonaHost.cs` (lines 281–287), pass `sp.GetRequiredService<OutboxWorker>()` when constructing `SimulatorIngressDispatchingFilterService`:
  ```csharp
  // In builder.ConfigureServices:
  var _ = services.AddScoped<IIngressFilterService>(sp =>
  {
      var inner = sp.GetRequiredService<IngressFilterService>();
      var holder = sp.GetRequiredService<InboundEnvelopeContextHolder>();
      var pipeline = sp.GetRequiredService<IInboundIngressPipeline>();
      var outboxWorker = sp.GetRequiredService<OutboxWorker>();
      return new SimulatorIngressDispatchingFilterService(inner, holder, pipeline, _logSink, DisplayName, outboxWorker);
  });
  ```

#### 4. Relayed Mailbox Drain Return-Receipt Outbox Flush (`Percolator.Simulator/Features/Topology/SimulatedPersonaHost.cs`)
In `SimulatedPersonaHost.DrainMailboxAsync` (around line 504), after the `foreach (var queued in drained)` loop finishes, if `processed > 0`, flush the outbox so any delivery receipts generated while draining the relay mailbox are immediately dispatched:
```csharp
if (processed > 0)
{
    var outboxWorker = scope.ServiceProvider.GetRequiredService<OutboxWorker>();
    await outboxWorker.ProcessBatchAsync(50, ct);
}
```
*Autopilot Loop Note*: `AutopilotPongResponder` already deduplicates by tracking `_lastRespondedPayload[nodeId] = latestIncoming.MessageId` and filtering `Where(m => m.AuthorId != host.PublicIdentityId)`, so receipt signals will not trigger duplicate Pong messages.

> [!CAUTION]
> **Anti-Shortcut Guidelines for WP-5**:
> - **Do NOT skip flushing `OutboxWorker` in `SimulatorIngressDispatchingFilterService` or `DrainMailboxAsync`**. In `Percolator.Simulator`, there is no background polling timer running `OutboxWorker` continuously; outbox jobs are only transmitted when `OutboxWorker.ProcessBatchAsync` or `FlushOutboxAsync` is explicitly invoked. If you omit this flush, the `DeliveryReceiptDto` job will sit in SQLite `OutboxJobs` forever and the sender will never see `✓✓`!
> - **Do NOT flush inside `SimulatorNotifyingPayloadHandlerDecorator`**: flush in `SimulatorIngressDispatchingFilterService` (after `_ingressPipeline.ProcessInboundAsync` finishes and releases the channel lock) and in `DrainMailboxAsync` (after the loop finishes).

---

### WP-6: Simulator Read Receipt Orchestration & UI Tick Presentation
**Context Scope**: `Percolator.Simulator/Features/DirectChat/` and `Percolator.Simulator/Features/Topology/` (4 files)

#### 1. Read Receipt Coordinator Method & Transcript Mapping (`Percolator.Simulator/Features/DirectChat/DirectChatCoordinator.cs`)
- In `GetTranscriptAsync` (lines 169–186), compute `isDelivered` and set `StatusText = "✓✓"` for both delivered and read outgoing messages:
  ```csharp
  var isRead = m.ReadAtUnixMs.HasValue;
  var isDelivered = m.DeliveredAtUnixMs.HasValue || isRead;
  var statusText = isOutgoing ? (isDelivered ? "✓✓" : "✓") : string.Empty;
  ```
  And set `IsDelivered = isDelivered` and `IsRead = isRead` on `ChatMessageItemViewModel`.
- Add `MarkConversationAsReadAsync` to `DirectChatCoordinator`:
  ```csharp
  public Task<bool> MarkConversationAsReadAsync(
      SimulatedNodeId readerNodeId,
      SimulatedNodeId senderNodeId,
      Guid upToMessageId,
      CancellationToken ct = default)
  {
      return Task.Run(async () =>
      {
          var reader = _worldState.GetPersonaHost(readerNodeId);
          var sender = _worldState.GetPersonaHost(senderNodeId);
          if (reader == null || sender == null) return false;

          try
          {
              var channelId = ChannelIdUtility.DeterministicFor(reader.PublicIdentityId, sender.PublicIdentityId);
              using var scope = reader.Services.CreateScope();
              var chatService = scope.ServiceProvider.GetRequiredService<IChatService>();

              var route = DeliveryRoute.DirectP2P;
              var routingRepo = scope.ServiceProvider.GetService<IPeerMailboxRoutingRepository>();
              if (routingRepo != null)
              {
                  var bestRoute = await routingRepo.GetBestRouteAsync(sender.PublicIdentityId, DateTimeOffset.UtcNow, ct);
                  if (bestRoute != null)
                  {
                      route = DeliveryRoute.RelayedOneToOne;
                  }
              }

              var command = new MarkAsReadCommand(
                  ChannelId: channelId,
                  ReaderId: reader.PublicIdentityId,
                  UpToPayloadId: PayloadId.FromGuid(upToMessageId),
                  RecipientIdentityId: sender.PublicIdentityId,
                  RecipientDeviceId: DeviceId.Primary,
                  Route: route);

              var result = await chatService.MarkAsReadAsync(command, ct);
              if (!result.IsSuccess) return false;

              var flushResult = await reader.FlushOutboxAsync(ct);
              return flushResult.IsSuccess;
          }
          catch (Exception ex)
          {
              _logSink.Log(new SimulatorLogEntry(
                  DateTimeOffset.UtcNow,
                  LogLevel.Warning,
                  reader.DisplayName,
                  reader.Id,
                  "DirectChat",
                  $"Failed to send read receipt: {ex.Message}"));
              return false;
          }
      }, ct);
  }
  ```

#### 2. ViewModel Read Trigger (`Percolator.Simulator/Features/Topology/PersonaCardViewModel.cs`)
- Add private field `private Guid _lastMarkedReadMessageId;`
- In `ExecuteRefreshChatTranscript` (right after `var transcript = await _chatCoordinator.GetTranscriptAsync(NodeId, activePeer.Id);` on line 304):
  ```csharp
  if (SendReadReceipts && IsListening)
  {
      var lastUnreadIncoming = transcript.LastOrDefault(m => !m.IsOutgoing && !m.IsRead);
      if (lastUnreadIncoming != null &&
          Guid.TryParse(lastUnreadIncoming.MessageId, out var msgGuid) &&
          msgGuid != _lastMarkedReadMessageId)
      {
          _lastMarkedReadMessageId = msgGuid;
          _ = _chatCoordinator.MarkConversationAsReadAsync(NodeId, activePeer.Id, msgGuid);
      }
  }
  ```
- Add partial property change hook so toggling `SendReadReceipts` to `true` immediately dispatches a read receipt for visible unread messages:
  ```csharp
  partial void OnSendReadReceiptsChanged(bool value)
  {
      if (value)
      {
          RefreshChatTranscript();
      }
  }
  ```

#### 3. Chat Item ViewModel (`Percolator.Simulator/Features/DirectChat/ChatMessageItemViewModel.cs`)
Update `ChatMessageItemViewModel` to expose `IsDelivered`, `BodyText`, `StatusColor`, and `StatusTooltip`:
```csharp
namespace Percolator.Simulator.Features.DirectChat;

internal sealed record ChatMessageItemViewModel
{
    public string MessageId { get; init; } = string.Empty;
    public string SenderName { get; init; } = string.Empty;
    public string Content { get; init; } = string.Empty;
    public string TimestampText { get; init; } = string.Empty;
    public bool IsOutgoing { get; init; }
    public string StatusText { get; init; } = string.Empty;
    public bool IsEdited { get; init; }
    public bool IsDelivered { get; init; }
    public bool IsRead { get; init; }
    public string ReactionsSummary { get; init; } = string.Empty;

    public string StatusColor => IsRead ? "#38BDF8" : "#888888";
    public string StatusTooltip => IsRead ? "Read (✓✓)" : (IsDelivered ? "Delivered (✓✓)" : "Sent (✓)");

    public string BodyText
    {
        get
        {
            var editedMarker = IsEdited ? " (edited)" : string.Empty;
            var reactions = string.IsNullOrEmpty(ReactionsSummary) ? string.Empty : $" [{ReactionsSummary}]";
            return $"[{TimestampText}] {(IsOutgoing ? "You" : SenderName)}: {Content}{editedMarker}{reactions}".Trim();
        }
    }

    public string DisplayText => string.IsNullOrEmpty(StatusText)
        ? BodyText
        : $"{BodyText} {StatusText}";

    public override string ToString() => DisplayText;
}
```

#### 4. XAML Transcript Template (`Percolator.Simulator/Features/Topology/PersonaCardView.axaml`)
Replace the `DataTemplate` inside `ItemsControl.ItemTemplate` (lines 89–94) so the receipt tick renders in its own right-aligned column with `StatusColor` (`#38BDF8` sky-blue when read, `#888888` grey when sent/delivered) and `ToolTip`:
```xml
<DataTemplate>
    <Border Background="{DynamicResource CardBackgroundFillColorDefaultBrush}"
            CornerRadius="4" Padding="6,4" Margin="0,2">
        <Grid ColumnDefinitions="*,Auto">
            <TextBlock Grid.Column="0" Text="{Binding BodyText}" FontSize="12" TextWrapping="Wrap" />
            <TextBlock Grid.Column="1" Text="{Binding StatusText}"
                       Foreground="{Binding StatusColor}"
                       ToolTip.Tip="{Binding StatusTooltip}"
                       FontWeight="Bold" FontSize="12"
                       Margin="6,0,0,0" VerticalAlignment="Bottom"
                       IsVisible="{Binding IsOutgoing}" />
        </Grid>
    </Border>
</DataTemplate>
```

> [!CAUTION]
> **Anti-Shortcut Guidelines for WP-6**:
> - **Do NOT remove `DisplayText` or `ToString()` from `ChatMessageItemViewModel`**. Scenario assertions and diagnostic logs rely on `DisplayText` / `ToString()`.
> - **Do NOT keep `Text="{Binding DisplayText}"` in `PersonaCardView.axaml`** when adding the second `TextBlock` for `StatusText`—that would duplicate `✓✓` twice (once in the body text and once in the right column). The left `TextBlock` (`Grid.Column="0"`) MUST bind to `{Binding BodyText}`, and the right `TextBlock` (`Grid.Column="1"`) MUST bind to `{Binding StatusText}`.
> - **Do NOT omit the `_lastMarkedReadMessageId` deduplication guard** in `PersonaCardViewModel.ExecuteRefreshChatTranscript`. Because `ExecuteRefreshChatTranscript` is triggered by `SignalCategory.ChatTranscriptChanged`, omitting `_lastMarkedReadMessageId` would cause `MarkConversationAsReadAsync` to fire repeatedly on every UI refresh.

**WP-5 & WP-6 Verification Command**:
```powershell
dotnet build Percolator.Simulator/Percolator.Simulator.csproj
```

---

## 3. Threat Model & Privacy Invariants

- **Cryptographic Sender Binding (`UNAUTHORIZED_AUTHOR`)**:
  - `HandleDeliveryReceiptAsync` verifies `dto.DelivererId == context.SenderIdentityId`.
  - `HandleReadReceiptAsync` verifies `dto.ReaderId == context.SenderIdentityId`.
  - Any mismatch between the outer Double Ratchet authenticated sender and the inner receipt identity is rejected before querying or mutating storage.
- **Cross-Channel Isolation (`CHANNEL_MISMATCH`)**:
  - Both receipt handlers verify `dto.ChannelId == context.ChannelId` and `targetMessage.ChannelId == context.ChannelId`.
- **Author Directionality Guard**:
  - Receipt watermark cascades (`TimestampUnixMs <= targetMessage.TimestampUnixMs`) only update messages where `msg.AuthorId != context.SenderIdentityId`, preventing a peer from mutating delivery/read timestamps on messages they authored themselves.
- **Stepped Padding & Traffic Analysis Resistance**:
  - Both `DeliveryReceiptDto` and `ReadReceiptDto` are packed through `SteppedPadding.CreatePaddedEnvelopeBytes(_serializer, ...)` and encrypted via the pairwise Double Ratchet session, making receipt envelopes indistinguishable in size and wire framing from standard chat payloads.
- **Store-and-Forward Zero-Presence Parity**:
  - When a recipient persona has `IsListening = false` (muted/offline), envelopes remain queued in the relay mailbox (`RelayMailboxQueue`). No `DeliveryReceiptDto` is emitted until the recipient comes online, drains the mailbox, decrypts the ratchet message, and commits it to local SQLite storage—matching Signal's exact store-and-forward semantics.

---

## 4. High-Value Unit Testing Matrix

| Work Package | Test Category | Target Feature / State | Target Test File & Method | Verifies (Observable Behavior) | Low-Brittle Guarantee |
| :--- | :--- | :--- | :--- | :--- | :--- |
| **WP-1** | Domain State | `ChatMessage.MarkAsDelivered` | `Percolator.Apps.Chat.Tests/Features/Messaging/ChatMessageTests.cs`<br/>`MarkAsDelivered_SetsDeliveredAtUnixMs_Idempotently_WithoutStartingExpirationTimer` | Sets `DeliveredAtUnixMs` on unacknowledged message; preserves existing timestamp on re-acknowledgment; leaves `ReadAtUnixMs` and `ExpiresAtUnixMs` null | Asserts domain aggregate properties directly; no mocks |
| **WP-1** | Domain State | `ChatMessage.MarkAsRead` | `Percolator.Apps.Chat.Tests/Features/Messaging/ChatMessageTests.cs`<br/>`MarkAsRead_SetsBothReadAndDeliveredAtUnixMs_AndStartsExpirationTimer_WhenExpireAfterSecondsConfigured` | Sets both `ReadAtUnixMs` and `DeliveredAtUnixMs`; initiates countdown `ExpiresAtUnixMs = readAtUnixMs + (expireAfterSeconds * 1000)` when configured | Asserts domain aggregate properties directly; no mocks |
| **WP-2** | Ingress Delivery Ack | `ChatPayloadHandler.HandleMessageAsync` | `Percolator.Apps.Chat.Tests/Ingress/ChatPayloadHandlerTests.cs`<br/>`HandleInboundAsync_WhenMessageReceivedOnDirectChannel_WithPayloadSender_EnqueuesDeliveryReceipt` | Persists inbound message to `IChatMessageRepository` and dispatches `ChatPayloadType.DeliveryReceipt` to author via `IPayloadSender` | Inspects captured `TestPayloadSender.SentPayloads`; zero mock call-count assertions |
| **WP-2** | Ingress Delivery Handler | `HandleDeliveryReceiptAsync` valid receipt | `Percolator.Apps.Chat.Tests/Ingress/ChatPayloadHandlerTests.cs`<br/>`HandleInboundAsync_WhenDeliveryReceiptReceived_WithValidSender_MarksTargetAndPrecedingMessagesDelivered` | Sets `DeliveredAtUnixMs` on target message and all preceding unacknowledged messages authored by the local identity in the channel | Asserts persisted repository state via `IChatMessageRepository.GetByIdAsync` |
| **WP-2** | Ingress Read Handler | `HandleReadReceiptAsync` valid receipt | `Percolator.Apps.Chat.Tests/Ingress/ChatPayloadHandlerTests.cs`<br/>`HandleInboundAsync_WhenReadReceiptReceived_WithValidSender_MarksTargetAndPrecedingMessagesReadAndDelivered` | Sets both `ReadAtUnixMs` and `DeliveredAtUnixMs` on target message and preceding unread messages authored by local identity | Asserts persisted repository state via `IChatMessageRepository.GetByIdAsync` |
| **WP-2** | Security Boundary | `HandleDeliveryReceiptAsync` spoofed sender | `Percolator.Apps.Chat.Tests/Ingress/ChatPayloadHandlerTests.cs`<br/>`HandleInboundAsync_WhenDeliveryReceiptReceived_WithSpoofedSender_ReturnsUnauthorizedAuthorAndDoesNotMutate` | Returns `DomainResult.Failure` with code `UNAUTHORIZED_AUTHOR`; does not mutate any message in repository | Asserts domain error code and invariant repository state |
| **WP-2** | Security Boundary | `HandleDeliveryReceiptAsync` mismatched channel | `Percolator.Apps.Chat.Tests/Ingress/ChatPayloadHandlerTests.cs`<br/>`HandleInboundAsync_WhenDeliveryReceiptReceived_WithMismatchedChannel_ReturnsChannelMismatchAndDoesNotMutate` | Returns `DomainResult.Failure` with code `CHANNEL_MISMATCH`; does not mutate any message in repository | Asserts domain error code and invariant repository state |
| **WP-3** | Serialization | `ProtobufPayloadSerializer` round-trip | `Percolator.Infrastructure.Tests/Plugins/Chat/ProtobufChatSerializerTests.cs`<br/>`DeliveryReceiptDto_RoundTripsThroughProtobufSerialization` | Serializes and deserializes `ChannelId`, `DelivererId`, `UpToPayloadId`, and `DeliveredAtUnixMs` losslessly | Round-trip object equality check; zero string or formatting assertions |
| **WP-4** | SQLite Persistence | `SqlChatMessageRepository` & `SqlChatMessageQueryService` | `Percolator.Infrastructure.Tests/Plugins/Chat/SqlChatMessageRepositoryTests.cs`<br/>`AddAsync_And_UpdateAsync_PersistAndRetrieve_DeliveredAtUnixMs` | Persists `DeliveredAtUnixMs` through SQLite and retrieves it accurately via repository and query service | In-memory SQLite round-trip test; tests observable database state |

---

## 5. Cross-Layer Infrastructure Alignment Summary

| Work Package | Layer / Project | File Path | Alignment Change |
| :--- | :--- | :--- | :--- |
| **WP-1** | Feature App (`Percolator.Apps.Chat`) | `Features/Messaging/ChatPayloadType.cs` | Add `DeliveryReceipt = 10` |
| **WP-1** | Feature App (`Percolator.Apps.Chat`) | `Features/Messaging/DeliveryReceiptDto.cs` | New wire DTO record |
| **WP-1** | Feature App (`Percolator.Apps.Chat`) | `Features/Messaging/ChatMessage.cs` | Add `DeliveredAtUnixMs`, `MarkAsDelivered`, update `MarkAsRead` |
| **WP-1** | Feature App (`Percolator.Apps.Chat`) | `Features/Messaging/ChatMessageReadModel.cs` | Add optional `DeliveredAtUnixMs` and `ReadAtUnixMs` |
| **WP-1** | Feature App (`Percolator.Apps.Chat`) | `Features/Messaging/ChatService.cs` | Author directionality check in `MarkAsReadAsync` |
| **WP-2** | Feature App (`Percolator.Apps.Chat`) | `Ingress/ChatPayloadHandler.cs` | Optional `IPayloadSender`, emit `DeliveryReceiptDto`, handle `DeliveryReceipt` |
| **WP-3** | Infrastructure (`Percolator.Infrastructure`) | `Protos/chat.proto` | Add `DeliveryReceiptProto` |
| **WP-3** | Infrastructure (`Percolator.Infrastructure`) | `Serialization/ProtobufPayloadSerializer.cs` | Map `DeliveryReceiptDto` <-> `DeliveryReceiptProto` |
| **WP-4** | Infrastructure (`Percolator.Infrastructure`) | `Persistence/Entities/ChatEntities.cs` | Add `DeliveredAtUnixMs` to `ChatMessageEntity` |
| **WP-4** | Infrastructure (`Percolator.Infrastructure`) | `Persistence/PercolatorDbContext.cs` | Add `HasConversion<long?>()` for `DeliveredAtUnixMs` |
| **WP-4** | Infrastructure (`Percolator.Infrastructure`) | `Persistence/Repositories/SqlChatMessageRepository.cs` | Map `DeliveredAtUnixMs` in `AddAsync`, `UpdateAsync`, `ToDomainModel` |
| **WP-4** | Infrastructure (`Percolator.Infrastructure`) | `Persistence/Repositories/SqlChatMessageQueryService.cs` | Project `DeliveredAtUnixMs` and `ReadAtUnixMs` |
| **WP-4** | Integration Tests (`Percolator.ApplicationIntegrationTests`) | `Adapters/Queries/InMemoryQueryAdapters.cs` | Project `DeliveredAtUnixMs` and `ReadAtUnixMs` |
| **WP-5** | Simulator (`Percolator.Simulator`) | `Features/DirectChat/ISimulatorChatQueryService.cs` | Add `DeliveredAtUnixMs` to `SimulatorChatMessageReadModel` |
| **WP-5** | Simulator (`Percolator.Simulator`) | `Features/DirectChat/SimulatorChatQueryService.cs` | Project `m.DeliveredAtUnixMs` from `PercolatorDbContext` |
| **WP-5** | Simulator (`Percolator.Simulator`) | `Features/Topology/SimulatorDirectIngressBridge.cs` | Flush `OutboxWorker` after `ProcessInboundAsync` |
| **WP-5** | Simulator (`Percolator.Simulator`) | `Features/Topology/SimulatedPersonaHost.cs` | Pass `OutboxWorker` to filter; flush outbox in `DrainMailboxAsync` |
| **WP-6** | Simulator (`Percolator.Simulator`) | `Features/DirectChat/DirectChatCoordinator.cs` | Add `MarkConversationAsReadAsync`; map `IsDelivered` & `✓✓` |
| **WP-6** | Simulator (`Percolator.Simulator`) | `Features/Topology/PersonaCardViewModel.cs` | Trigger `MarkConversationAsReadAsync` when viewing unread messages |
| **WP-6** | Simulator (`Percolator.Simulator`) | `Features/DirectChat/ChatMessageItemViewModel.cs` | Add `IsDelivered`, `StatusColor`, `StatusTooltip`, `BodyText` |
| **WP-6** | Simulator (`Percolator.Simulator`) | `Features/Topology/PersonaCardView.axaml` | Two-column `Grid` with colored `StatusText` `TextBlock` |

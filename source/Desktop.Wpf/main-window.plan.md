# Bi-Directional Chat Plan (Main a+" Simulator)

**Goal:** Secure bi-directional chat messaging between the Main Window and Simulated Peers over established Direct and Relayed sessions.

## Architecture Guidelines Checklist

| Guideline | Source | Rule                                                                                                                                                                                |
|---|---|-------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|
| Thread-agnostic services | `r3.readme.md` | State services must not inject `IUiDispatcher`. Mutations under `SemaphoreSlim`/`lock`.                                                                                             |
| ViewModel UI bridging | `wpf.readme.md` | ViewModels own the dispatcher. Collections via `CreateView` to `ToNotifyCollectionChanged(ui.CollectionEventDispatcher)`. Properties via `ObserveOnCurrentSynchronizationContext()`. |
| Domain snapshotting | `r3.readme.md` | `.Freeze()` under lock produces pure immutable records for background I/O.                                                                                                          |
| No ViewModel sorting | `wpf.readme.md` | Sorting in XAML via `CollectionViewSource.GetDefaultView(...)` + `CustomSort`.                                                                                                      |
| Robust tests | `unit-testing.md` | AAA pattern, black-box, no internal-state assertions. Mock external deps only.                                                                                                      |
| Domain isolation | Architecture | Domain projects never reference each other or Application. Cross-domain goes via Application.                                                                                       |
| Contracts project | Architecture | Only protobuf defs. No C# interfaces.                                                                                                                                               |

## Pre-existing Concern: Potential Double-Send

`PostTextMessageHandler` (Application layer) currently sends `ChatEnvelope` directly to all participants (lines 69-76) AND publishes `TextMessagePostedEvent`. `TextMessagePostedHandler` catches that event and dispatches `DispatchTextMessageCommand`, which sends again via `IRemoteEnvelopeSender` (with PKH resolution for relay fallback).

Treat this as a **bug** (duplicate send risk). The preferred fix (when you address it) is:

- Keep the `TextMessagePostedEvent` to `DispatchTextMessageCommand` pipeline as the **only** outbound network send.
- Remove the direct `IRemoteEnvelopeSender` send loop from `PostTextMessageHandler`.

This plan intentionally scopes that fix out; the bi-directional plan remains correct even if the duplicate-send is resolved later.

## Identity & Author Attribution Invariants (verifiable in codebase)

- Peers never exchange `PeerId` values. `PeerId` is a local-only identifier.
- The only cross-peer author identifier is the author's identity signing public key (`SPKI`) or its hash (`PKH = SHA256(SPKI)`).
- For group chat, the author is identified by `TextMessage.AuthorIdentityKey` (SPKI). This is already stamped on outbound messages by `PostTextMessageHandler`.

**Inbound author resolution algorithm (group / relayed / any message that includes SPKI):**

1) Read `authorSpki = chatEnvelope.TextMessage.AuthorIdentityKey`.
2) Compute `authorPkh = SHA256(authorSpki)`.
3) Resolve local peer id via `IPeerPublicSigningKeyStore.GetPeerIdByPublicKeyHashAsync(authorPkh)`.
    - If null: fail fast (throw) in the Application layer. The sender must be introduced/known (e.g., by `SetPeerNameByPublicKeyCommand`) before we can attribute.
4) Convert to Chat domain participant id via `new ParticipantId(peerId.Value)`.

**Layering rule:** Chat domain stays isolated from Identity types. Implement the lookup via an Application-layer adapter over `IPeerPublicSigningKeyStore` (Chat-side boundary is `IPkhPeerResolver`).

**Important type constraint:** In this codebase, `ParticipantId` is a `Guid` wrapper. Any PKHa to peer lookup used for chat author attribution must return a `Guid`-backed identifier (not an `int`).
 
---

## Chunk A:

### A.1 Outcome

Implement a robust invariant:

- whenever the system creates/persists a cryptographic `SecureSession` (`SessionId`) with a remote peer, it must also persist the `(SelfIdentityId, RemotePeerId) -> DirectSessionId` mapping via `IDirectSessionRepository`.

This fixes the crash when chatting with relayed simulated peers and prevents other downstream failures that depend on `DirectSessions` mappings.

### A.2 User-visible failures this chunk fixes

- **Chat UI crash**: selecting a relayed peer chat triggers reload, which throws:
    - `InvalidOperationException: No DirectSession found for SessionId=...`
- **Send crash**: clicking **Send** for a relayed peer triggers the same resolver path and throws.

**Why this mapping matters beyond Chat:** inbound decrypt/routing also depends on it. `Percolator.Application.Network.DeliverOpaqueMessageHandler` calls `IDirectSessionRepository.GetBySessionIdAsync(...)` and throws if missing.

### A.3 TDD plan (Red -> Green -> Refactor)

#### A.3.1 Red: add a failing unit test that captures the invariant

Add a test in the appropriate test project for Application-layer handlers (choose the existing `*.Tests` project that already tests MediatR handlers in `Percolator.Application`).

**Test intent:** calling `ApprovePendingSessionHandler.Handle(...)` must persist a direct-session mapping for the newly created `sessionId`.

**Arrange requirements (minimum):**

- mock `IDirectSessionRepository` and capture the args passed to `UpsertAsync(...)`
- mock/stub `IPendingSessionRepository.GetAsync(...)` to return a `PendingSession` with:
    - `RemotePeerId` set
    - `IsRelayed` can be either `true` or `false` (test both if you want)
    - `Invitation` containing bytes that parse as `EstablishDirectSessionRequest` with a valid `InviteHandshakeRequestPayload`
- provide an active identity via:
    - `_activeIdentityAccessor.IsActive == true`
    - `_active.Identity` non-null
    - `_active.Keys` non-null

**Assert:**

- `IDirectSessionRepository.UpsertAsync(...)` is called exactly once
- the `DirectSessionId` passed equals the `SessionId` created by the handler
- the `remotePeerId` passed equals `pending.RemotePeerId`
- the `selfIdentityId` passed equals `_active.Identity.SelfIdentityId.Value`

Example assertion skeleton:

 ```csharp
 DirectSessionId? capturedSid = null;
 PeerId? capturedRemote = null;
 int? capturedSelf = null;

 directSessionRepo
     .Setup(r => r.UpsertAsync(It.IsAny<PeerId>(), It.IsAny<DirectSessionId>(), It.IsAny<int>()))
     .Callback<PeerId, DirectSessionId, int>((remote, sid, self) =>
     {
         capturedRemote = remote;
         capturedSid = sid;
         capturedSelf = self;
     })
     .Returns(Task.CompletedTask);
 
 // ... invoke handler
 
 capturedRemote.Should().Be(new PeerId(pending.RemotePeerId.Value));
 capturedSelf.Should().Be(active.Identity.SelfIdentityId.Value);
 capturedSid.Should().NotBeNull();
 ```

#### A.3.2 Green: implement the invariant once (new application service)

Do **not** add another one-off upsert in the handler. Instead, implement an Application-layer service that owns the mapping invariant.

**Create new file:** `source/Percolator.Application/Services/DirectSessionMappingWriter.cs`

 ```csharp
 using Microsoft.Extensions.Logging;
 using Percolator.Cryptography;
 using Percolator.Cryptography.Primitives;
 using Percolator.Network;

 namespace Percolator.Application.Services;

 public interface IDirectSessionMappingWriter
 {
     Task PersistAsync(int selfIdentityId, PeerId remotePeerId, SessionId sessionId, CancellationToken ct = default);
 }

 public sealed class DirectSessionMappingWriter : IDirectSessionMappingWriter
 {
     private readonly ILogger<DirectSessionMappingWriter> _logger;
     private readonly IDirectSessionRepository _repo;

     public DirectSessionMappingWriter(ILogger<DirectSessionMappingWriter> logger, IDirectSessionRepository repo)
     {
         _logger = logger;
         _repo = repo;
     }

     public async Task PersistAsync(int selfIdentityId, PeerId remotePeerId, SessionId sessionId, CancellationToken ct = default)
     {
         try
         {
             await _repo.UpsertAsync(remotePeerId, new DirectSessionId(sessionId.Value), selfIdentityId).ConfigureAwait(false);
         }
         catch (Exception ex)
         {
             // Keep behavior consistent with existing finalize code: persistence is best-effort.
             _logger.LogInformation(ex, "Best-effort direct session mapping upsert failed. self={Self} remote={Remote} sid={Sid}", selfIdentityId, remotePeerId.Value, sessionId.Value);
         }
     }
 }
 ```

**DI registration (Desktop.Wpf):** register `IDirectSessionMappingWriter` in the Desktop app host.

- **File:** `source/Desktop.Wpf/App.xaml.cs`
- **Location:** inside `.ConfigureServices((context, services) => { ... })`
- **Add registration:**

 ```csharp
 services.AddSingleton<IDirectSessionMappingWriter, DirectSessionMappingWriter>();
 ```

This plan intentionally does **not** require changes to `Percolator.Node`.

#### A.3.3 Green: use the writer in `ApprovePendingSessionHandler`

**Modify file:** `source/Percolator.Application/Network/ApprovePendingSessionCommand.cs`

- inject `IDirectSessionMappingWriter`
- immediately after:
    - `await _sessions.AddAsync(session, cancellationToken).ConfigureAwait(false);`
- call:

 ```csharp
 await _directSessionMappingWriter.PersistAsync(
     _active.Identity!.SelfIdentityId.Value,
     new Percolator.Network.PeerId(pending.RemotePeerId.Value),
     sessionId,
     cancellationToken).ConfigureAwait(false);
 ```

**Do not** remove the existing `IDirectSessionLocator` from this handler yet: it is used later in `SendInviteHandshakeResponseViaRelayHostAsync(...)`.

#### A.3.4 Refactor: eliminate duplication in other session-creation flows

Update these to use `IDirectSessionMappingWriter` (same invariant, one implementation):

- `source/Percolator.Application/Network/Handshake/InitiatorFinalizeService.cs`
    - replace the existing `try { _directSessions.UpsertAsync(...) } catch { ... }` blocks with a call to the writer
- `source/Percolator.Application/Network/StandardHandshakeIngress.cs`
    - replace direct `_directSessions.UpsertAsync(...)` with the writer

This ensures you cannot forget the mapping again in a new handshake path.

### A.4 Verification (automated + manual)

#### A.4.1 Automated

- run the new unit test(s)
- ensure the previously failing test is green

#### A.4.2 Manual (WPF)

- run the app
- create/accept a relayed simulated peer session
- select that peer in the Secure Channel UI
- confirm `ChatReloadCoordinator` does not crash
- click **Send**
- confirm there is no `No DirectSession found for SessionId=...` exception

### A.5 Relay message-flow verification (grounded in current code)

After A.3 is merged, verify **both directions** explicitly:

#### A.5.1 Main -> Simulated peer (via relay)

- In the WPF app, select a simulated peer whose connection mode is `ViaRelay`.
- Send a chat message from Main.
- Simulator already has a relay-decrypt path in:
    - `Desktop.Wpf/Features/Simulator/SimulatorStateService.ReceiveRelayedOpaquePayloadAsync(...)`

That method already:

- parses `opaqueBytes` as `SessionRatchetMessage`
- attempts `kv.Value.Decrypt(cipher, clock)` across `peerModel.SessionsMutable`
- parses plaintext as `InternalEnvelope`
- for `InternalEnvelope.ChatEnvelope.TextMessage`, appends via `peerModel.AddChatMessage(...)`

If Main->Sim relay chat does not show up **after the DirectSession mapping invariant is fixed**, the issue is upstream delivery of relayed bytes into the simulatoraEUR(tm)s `ReceiveRelayedOpaquePayloadAsync`.

#### A.5.2 Simulated peer -> Main (via relay)

- In the simulator UI, use the simulated peer "send chat to maina" action.
- That path is:
    - `Desktop.Wpf/Features/Simulator/SimulatorStateService.SendChatMessageToMainAsync(...)`

It already:

- builds `ChatEnvelope` inside `InternalEnvelope`
- encrypts via `EncryptInternalEnvelopeAsync(...)`
- if `ConnectionMode.ViaRelay`, calls `EnqueueRelayUpstreamToMainAsync(relayHostPeerId, opaqueBytes: cipher.Value, debugType: "Chat", ...)`

**Definition of done (relay):**

- Main can send a chat message to a relayed simulated peer and see it in the simulator card history
- a relayed simulated peer can send a chat message to Main and see it in Main's chat history

---

## Chunk B: Make DirectSession mapping persistence hard-to-break + make handshake finalize code testable

### B.0 Problem statement

Chunk A ensured the DirectSession mapping is written in at least one previously-missed finalize flow (`TryFinalizeFromFirstResponderAsync`) and centralized mapping writes behind `IDirectSessionMappingWriter`.

However:

- Some finalize flows are difficult to unit test because they combine:
    - protobuf parsing
    - signature verification
    - peer identity upsert
    - ratchet/session creation
    - persistence + notifications
- Some unit tests are brittle due to strict mocks and `VerifyAll()`.
- We want the invariant to be resilient even if mapping persistence fails (best-effort).

**Chunk B goal:** reduce brittleness, improve testability, and add minimal high-value tests that prevent regressions of the DirectSession mapping invariant.

### B.1 Testing standard for Chunk B (apply `unit-testing.md`)

- Tests must follow AAA.
- Prefer black-box behavior assertions.
- Avoid `VerifyAll()`.
- Only verify interactions when the interaction *is* the behavior (e.g., mapping persistence).

### B.2 Simplify the existing Chunk A tests (reduce brittleness)

#### B.2.1 InitiatorFinalizeServiceTests

File:

- `source/Percolator.ApplicationTests/Handshake/InitiatorFinalizeServiceTests.cs`

Change the test:

- `TryFinalizeFromFirstResponderAsync_Decrypts_Persists_Session_And_Deletes_PreHandshake`

Required edits:

- Remove these calls:
    - `preStore.VerifyAll()`
    - `sessions.VerifyAll()`
    - `index.VerifyAll()`
    - `directSessionMappingWriter.VerifyAll()`
- Keep the assertions that represent public behavior:
    - `result` is not null
    - result session id equals responder-assigned session id
    - `IDirectSessionMappingWriter.WriteMappingAsync(...)` was called and the captured `DirectSessionId` equals the responder-assigned session id
- If you keep any `Verify(... Times.Once)` calls, limit them to:
    - mapping write (primary behavior)
    - prehandshake delete (optional; only keep if deletion is a contract)

#### B.2.2 StandardHandshakeIngressTests

File:

- `source/Percolator.ApplicationTests/Network/StandardHandshakeIngressTests.cs`

Change the test:

- `HandleAsync_WhenValidRequest_PersistsDirectSessionMapping`

Required edits:

- Remove `directSessionMappingWriter.VerifyAll()`.
- Keep:
    - `result.MessageCase == EstablishSessionResponse.MessageOneofCase.Response`
    - `WriteMappingAsync(...)` was called and captured `selfIdentityId` matches

#### B.2.3 DirectSessionMappingWriterTests

File:

- `source/Percolator.ApplicationTests/Services/DirectSessionMappingWriterTests.cs`

Change required:

- Remove `WriteMappingAsync_WhenRepositorySucceeds_DoesNotLogWarning`.
    - Rationale: asserting *absence* of logs is brittle and not part of a strong public contract.
- Keep a single test:
    - `WriteMappingAsync_WhenRepositoryThrows_DoesNotThrow` (may still verify a warning log occurred, but do not assert specific message contents).

### B.3 Harden handshake flows so a mapping write cannot break session establishment

#### B.3.1 Make mapping persistence best-effort at call sites

Files:

- `source/Percolator.Application/Network/Handshake/InitiatorFinalizeService.cs`
- `source/Percolator.Application/Network/StandardHandshakeIngress.cs`

Required change:

- Wrap the call to `_directSessionMappingWriter.WriteMappingAsync(...)` in `try/catch`.
- In `catch (Exception ex)`:
    - log **Information** (not Warning) that mapping persistence failed (include `RemotePeerId`, `SessionId`, `SelfIdentityId`),
    - **do not** rethrow.

Acceptance criteria:

- If mapping persistence throws, handshake finalization still succeeds (session is persisted, response is returned).

### B.4 Add minimal high-value tests for the call-site best-effort behavior

#### B.4.1 StandardHandshakeIngress: mapping writer failure does not fail handshake

File:

- `source/Percolator.ApplicationTests/Network/StandardHandshakeIngressTests.cs`

Add test:

- `HandleAsync_WhenMappingWriterThrows_ReturnsResponse`

Arrange:

- Same setup as the existing `HandleAsync_WhenValidRequest_PersistsDirectSessionMapping`.
- Configure the `IDirectSessionMappingWriter` mock:
    - `.Setup(w => w.WriteMappingAsync(...)).ThrowsAsync(new InvalidOperationException("boom"))`

Act:

- Call `sut.HandleAsync(...)`.

Assert:

- Response is non-null.
- Response has `MessageCase == Response`.
- (Do NOT assert logging.)

#### B.4.2 InitiatorFinalizeService: mapping writer failure does not fail finalize

File:

- `source/Percolator.ApplicationTests/Handshake/InitiatorFinalizeServiceTests.cs`

Add test:

- `TryFinalizeFromFirstResponderAsync_WhenMappingWriterThrows_ReturnsSessionId`

Arrange:

- Copy the existing test setup for the responder-first finalize.
- Configure mapping writer to throw.

Act:

- Call `TryFinalizeFromFirstResponderAsync(...)`.

Assert:

- result is non-null
- session id equals responder-assigned session id

### B.5 Make remaining finalize flows unit-testable by extracting "validation/parsing" logic

#### B.5.1 Extract EstablishSessionResponse validation/parsing

Files:

- New interface: `source/Percolator.Application/Network/Handshake/IEstablishSessionResponseValidator.cs`
- Implementation: `source/Percolator.Application/Network/Handshake/EstablishSessionResponseValidator.cs`

Interface contract:

- Method:
    - `Task<EstablishSessionResponseValidationResult?> TryValidateAsync(EstablishSessionResponse response, CancellationToken ct)`
- Where `EstablishSessionResponseValidationResult` contains:
    - `SessionId SessionId`
    - `byte[] RemoteIdentitySpki`
    - `byte[] RemotePublicKeyHash`

Implementation requirements (move code out of `TryFinalizeFromEstablishSessionResponseAsync`):

- Validate required fields
- Verify signature over `ResponsePayload` bytes
- Parse `ResponsePayload` and validate `SessionId` present
- Return `null` on any validation failure

#### B.5.2 Update InitiatorFinalizeService to use the validator

File:

- `source/Percolator.Application/Network/Handshake/InitiatorFinalizeService.cs`

Required change:

- Inject `IEstablishSessionResponseValidator` into constructor.
- Replace the inline signature verification + payload parse in `TryFinalizeFromEstablishSessionResponseAsync` with a call to the validator.

Update DI registrations (composition root) accordingly.

### B.6 Add unit tests for the validator (pure logic; low mocking)

File:

- `source/Percolator.ApplicationTests/Handshake/EstablishSessionResponseValidatorTests.cs`

Add tests (minimum):

- `TryValidateAsync_WhenSignatureInvalid_ReturnsNull`
- `TryValidateAsync_WhenPayloadMissingSessionId_ReturnsNull`
- `TryValidateAsync_WhenValid_ReturnsSessionIdAndRemoteHash`

Notes:

- Use real protobuf messages.
- Generate real keys for signature validation.
- Do not mock protobuf parsing.

### B.7 Verification

#### B.7.1 Automated

- `dotnet test` must be green for:
    - `Percolator.ApplicationTests`

#### B.7.2 Manual (WPF)

- Repeat the manual checks from A.4.2 and A.5 for relayed chat.

## Latest error (remaining - may be the same reason for chunk A and B)

```
fail: Desktop.Wpf.App[0]
      R3 Unhandled exception
      System.InvalidOperationException: No DirectSession found for SessionId=21b36ee4-4027-4b2e-8557-1aec7fb1a027.
         at Percolator.Infrastructure.Chat.ChatConversationResolver.ResolveAsync(ConversationLookupKey lookupKey, CancellationToken cancellationToken) in C:\Users\squir\source\repos\percolator\source\Percolator.Infrastructure\Chat\ChatConversationResolver.cs:line 34
         at Desktop.Wpf.Features.Chat.ChatReloadCoordinator.ReloadCoreAsync(String sessionId, CancellationToken cancellationToken) in C:\Users\squir\source\repos\percolator\source\Desktop.Wpf\Features\Chat\ChatReloadCoordinator.cs:line 61
         at Desktop.Wpf.Features.Chat.ChatReloadCoordinator.<.ctor>b__5_0(String sessionId, CancellationToken ct) in C:\Users\squir\source\repos\percolator\source\Desktop.Wpf\Features\Chat\ChatReloadCoordinator.cs:line 42
         at R3.SelectAwait`2.SelectAwaitDrop.OnNextAsync(T value, CancellationToken cancellationToken, Boolean configureAwait)
         at System.Runtime.CompilerServices.PoolingAsyncValueTaskMethodBuilder`1.StateMachineBox`1.System.Threading.Tasks.Sources.IValueTaskSource.GetResult(Int16 token)
         at R3.AwaitOperationDropObserver`1.StartAsync(T value)

```

### Latest error — Call chain and theories (Main -> relayed simulated peer)

- **WPF command (Send)**
  - File: `Desktop.Wpf/Features/Chat/ChatViewModel.cs`
  - `SendCommand` executes and sends MediatR `PostTextMessageCommand(ConversationLookupKey.ForDirectSession(sessionGuid), ...)`.

- **MediatR handler**
  - File: `Percolator.Application/Apps/Chat/Handlers/PostTextMessageHandler.cs`
  - Resolves conversation via `_resolver.ResolveAsync(lookupKey)` (Infrastructure `ChatConversationResolver` requires a valid DirectSession mapping).
  - Writes the message via `_writer.AddTextMessageAsync(...)`.
  - Sends network via `_sender.SendChatEnvelopeToPeerAsync(chatEnvelope, new RecipientRoute(peerId, pkh: null))`.

- **Remote envelope sender -> transport**
  - File: `Percolator.Application/Network/RemoteEnvelopeSender.cs`
  - Wraps into `InternalEnvelope` and calls `_messageService.SendMessageAsync(internalEnvelope, recipient.PeerId, ...)`.

- **Simulator outbound interception**
  - File: `Desktop.Wpf/Features/Simulator/SimulatorOutboundInterceptor.cs`
  - `TryDeliverOpaqueMessage(...)` intercepts and delegates to `ISimulatorStateService.ReceiveOpaqueMessageFromMainAsync(...)` for simulated peers.

- **Simulator relay reference (peer -> main path)**
  - File: `Desktop.Wpf/Features/Simulator/SimulatorStateService.cs`
  - `SendChatMessageToMainAsync(...)` shows the analogous relay routing (encrypt, enqueue to relay, or direct deliver). The Main -> Sim ingress is handled by `ReceiveOpaqueMessageFromMainAsync`.

#### Theories for the error (with evidence)

- **[T1] Missing DirectSession mapping for SessionId**
  - Evidence: Exception text "No DirectSession found for SessionId=..." originates in `ChatConversationResolver.ResolveAsync(...)`, which is invoked by `PostTextMessageHandler` and by `ChatReloadCoordinator` when a chat opens. If the `(SelfIdentityId, RemotePeerId) -> DirectSessionId` mapping was not written during handshake finalization, both reload and send will fail along this path.
  - Context: Chunk A above specifies persisting this mapping in finalize flows because it was previously missed.

- **[T2] Duplicate-send path does not repair mapping**
  - Evidence: `PostTextMessageHandler` both sends directly and also publishes `TextMessagePostedEvent` (which triggers another send). Even if transport proceeds, conversation resolution still depends on the missing DirectSession mapping, so UI reload continues to throw.

- **[T3] Transport/relay not implicated by the stack**
  - Evidence: Stack trace stops at conversation resolution, not in `_messageService` or simulator interceptor. Fixing the mapping should unblock both direct and relayed routes.

#### Actionable checks

- Ensure all session finalize paths persist `(SelfIdentityId, RemotePeerId) -> DirectSessionId` (see Chunk A.3/A.3.4).
- For the failing `SessionId` in logs, probe `IDirectSessionRepository.GetBySessionIdAsync(...)` (temporary log/diagnostic) to confirm absence.
- After wiring the mapping writer, re-test: opening the chat should not throw; Send should reach the simulator (interceptor should log `TryDeliverOpaqueMessage`).

## Chunk C

### Goal

Fix the UI reload bug caused by treating a `ConversationId` as a `DirectSessionId`.

The architecture intent is:

- **Commands/transport routing** may start from a `ConversationLookupKey` (e.g., `ForDirectSession(sessionGuid)`), but once the application has resolved to a concrete `Conversation`, UI reloads should key off the **conversation identity**, not the session identity.
- Use **domain primitives** instead of naked GUIDs where available (`ConversationId`, etc.).

### Root cause (confirmed)

- `PostTextMessageCommand` is correctly sent with `ConversationLookupKey.ForDirectSession(sessionGuid)`.
- `PostTextMessageHandler` resolves the lookup to a `ConversationResolution` and publishes `TextMessagePostedEvent` containing **`ConversationId`** (not a session id).
- `Desktop.Wpf/Features/Chat/Handlers/ChatStateUpdateHandlers.cs` currently calls:
  - `_reload.TriggerReloadForSession(notification.ConversationId.ToString());`
  - This incorrectly feeds a **conversation id** into the reload path which assumes a **direct session id** and calls `ConversationLookupKey.ForDirectSession(Guid.Parse(...))`.

### Clean fix strategy

Introduce a conversation-based reload API that does not require any session lookup.

- Add `TriggerReloadForConversation(ConversationId conversationId, int selfIdentityId)` to the reload coordinator.
- Reload core should read the conversation directly by `(ConversationId, selfIdentityId)` via `IConversationRepository.GetByIdAsync`.
- Update `ChatStateUpdateHandlers` to call the new conversation-based reload method using:
  - `new ConversationId(notification.ConversationId)`
  - `notification.SenderSelfIdentityId`

This keeps:

- **Chat domain events** (`TextMessagePostedEvent`) conversation-centric.
- **UI reload** conversation-centric.
- **Session ids** limited to crypto/transport routing and handshake code paths.

### Step C.1: Update IChatReloadCoordinator and ChatReloadCoordinator (conversation-based)

File: `Desktop.Wpf/Features/Chat/ChatReloadCoordinator.cs`

1. Update the interface:

- Replace:
  - `void TriggerReloadForSession(string sessionId);`
- With:
  - `void TriggerReloadForConversation(Percolator.Chat.ValueObjects.ConversationId conversationId, int selfIdentityId);`

2. Change the trigger stream type:

- Replace `_reloadTrigger` from `Subject<string>` to `Subject<(Percolator.Chat.ValueObjects.ConversationId conversationId, int selfIdentityId)>`.

3. Update subscription pipeline:

- Replace the `SelectAwait` lambda parameter from `(sessionId, ct)` to `((conversationId, selfIdentityId), ct)`.
- Call a new reload core: `ReloadCoreAsync(conversationId, selfIdentityId, ct)`.

4. Replace `TriggerReloadForSession` implementation with `TriggerReloadForConversation`:

- `public void TriggerReloadForConversation(ConversationId conversationId, int selfIdentityId)
  {
      _reloadTrigger.OnNext((conversationId, selfIdentityId));
  }`

5. Rewrite `ReloadCoreAsync` to avoid `IConversationResolver` entirely:

- Replace signature:
  - `private async Task ReloadCoreAsync(string sessionId, CancellationToken cancellationToken)`
- With:
  - `private async Task ReloadCoreAsync(Percolator.Chat.ValueObjects.ConversationId conversationId, int selfIdentityId, CancellationToken cancellationToken)`

- Replace the resolution call:
  - Remove `Guid.Parse(sessionId)`
  - Remove `IConversationResolver.ResolveAsync(...)`

- Load conversation directly:
  - `var conversation = await conversationRepository.GetByIdAsync(conversationId, selfIdentityId).ConfigureAwait(false);`

- When syncing state, keep the existing chat-state key stable. Use `conversationId.Value.ToString("N")` consistently (not `ToString()` which includes hyphens):
  - `var conversationKey = conversationId.Value.ToString("N");`
  - `_state.SyncMessages(conversationKey, snapshots);`

6. Ensure the method uses domain primitives:

- Add `using Percolator.Chat.ValueObjects;` at the top of the file.

### Step C.2: Update ChatStateUpdateHandlers to use conversation-based reload

File: `Desktop.Wpf/Features/Chat/Handlers/ChatStateUpdateHandlers.cs`

1. Replace both reload calls:

- For posted:
  - Replace `_reload.TriggerReloadForSession(notification.ConversationId.ToString());`
  - With:
    - `_reload.TriggerReloadForConversation(new Percolator.Chat.ValueObjects.ConversationId(notification.ConversationId), notification.SenderSelfIdentityId);`

- For received:
  - Replace `_reload.TriggerReloadForSession(notification.ConversationId.ToString());`
  - With:
    - `_reload.TriggerReloadForConversation(new Percolator.Chat.ValueObjects.ConversationId(notification.ConversationId), notification.SelfIdentityId);`
      - (Use the actual property name on `TextMessageReceivedEvent`; if it differs, update accordingly. The important requirement is: pass the correct `selfIdentityId` for the local DB read.)

2. Add `using Percolator.Chat.ValueObjects;` (and then use `ConversationId` without fully qualifying if you prefer consistency).

### Step C.3: Update ChatViewModel initial reload trigger

File: `Desktop.Wpf/Features/Chat/ChatViewModel.cs`

Currently `SetSession(string sessionId)` triggers an initial reload via `_reloadCoordinator.TriggerReloadForSession(sessionId);`.

For this chunk, make chat reload consistent with the new conversation-based reload:

1. Change `SetSession` to accept a conversation id key, not a session id:

- Replace `SetSession(string sessionId)` with:
  - `SetConversation(Percolator.Chat.ValueObjects.ConversationId conversationId, int selfIdentityId)`

2. Replace internal storage `_sessionId` with `_conversationId`:

- Replace `private string? _sessionId;` with:
  - `private ConversationId? _conversationId;`
  - `private int? _selfIdentityId;`

3. Update `SendCommand` to use conversation id routing without assuming direct session id:

- Replace the direct-session lookup key construction based on `_sessionId`.
- Instead, send a command that routes by conversation id.

NOTE: If no command exists today for posting by conversation id, do not invent one in this chunk. Keep the current direct-session send path for now by leaving `SendCommand` unchanged, but ensure initial reload uses conversation-based reload only when you already have a conversation id available.

Pragmatic minimum for this chunk (no new commands):

- Leave `SendCommand` as-is.
- Only update the initial reload trigger call site(s) that currently pass a conversation id as if it were a session id (Step C.2). This fully resolves the observed bug.

### Step C.4: Verify behavior (manual debug checklist)

- Put a breakpoint in `ChatStateUpdateHandlers.Handle(TextMessagePostedEvent ...)` and confirm:
  - `notification.ConversationId` is the conversation GUID (not a session GUID).
  - The handler calls `TriggerReloadForConversation(new ConversationId(notification.ConversationId), notification.SenderSelfIdentityId)`.

- Put a breakpoint in `ChatReloadCoordinator.ReloadCoreAsync(ConversationId conversationId, int selfIdentityId, ...)` and confirm:
  - `conversationRepository.GetByIdAsync(conversationId, selfIdentityId)` returns a non-null conversation.
  - No call to `IConversationResolver.ResolveAsync` occurs during reload.

- Send a message from the chat UI:
  - Should no longer throw `No DirectSession found for SessionId=...` during reload.
  - The message should appear after reload.

## Chunk C
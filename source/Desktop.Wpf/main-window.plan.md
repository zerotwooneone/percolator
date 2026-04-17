# Bi-Directional Chat Plan (Main ↔ Simulator)

**Goal:** Secure bi-directional chat messaging between the Main Window and Simulated Peers over established Direct and Relayed sessions.

## Architecture Guidelines Checklist

| Guideline | Source | Rule |
|---|---|---|
| Thread-agnostic services | `r3.readme.md` | State services must not inject `IUiDispatcher`. Mutations under `SemaphoreSlim`/`lock`. |
| ViewModel UI bridging | `wpf.readme.md` | ViewModels own the dispatcher. Collections via `CreateView` → `ToNotifyCollectionChanged(ui.CollectionEventDispatcher)`. Properties via `ObserveOnCurrentSynchronizationContext()`. |
| Domain snapshotting | `r3.readme.md` | `.Freeze()` under lock produces pure immutable records for background I/O. |
| No ViewModel sorting | `wpf.readme.md` | Sorting in XAML via `CollectionViewSource.GetDefaultView(...)` + `CustomSort`. |
| Robust tests | `unit-testing.md` | AAA pattern, black-box, no internal-state assertions. Mock external deps only. |
| Domain isolation | Architecture | Domain projects never reference each other or Application. Cross-domain goes via Application. |
| Contracts project | Architecture | Only protobuf defs. No C# interfaces. |

## Pre-existing Concern: Potential Double-Send

`PostTextMessageHandler` (Application layer) currently sends `ChatEnvelope` directly to all participants (lines 69-76) AND publishes `TextMessagePostedEvent`. `TextMessagePostedHandler` catches that event and dispatches `DispatchTextMessageCommand`, which sends again via `IRemoteEnvelopeSender` (with PKH resolution for relay fallback).

Treat this as a **bug** (duplicate send risk). The preferred fix (when you address it) is:

- Keep the `TextMessagePostedEvent` → `DispatchTextMessageCommand` pipeline as the **only** outbound network send.
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

 **Important type constraint:** In this codebase, `ParticipantId` is a `Guid` wrapper. Any PKH→peer lookup used for chat author attribution must return a `Guid`-backed identifier (not an `int`).
 
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
 
 ### A.3 TDD plan (Red → Green → Refactor)
 
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
 
 If Main->Sim relay chat does not show up **after the DirectSession mapping invariant is fixed**, the issue is upstream delivery of relayed bytes into the simulator’s `ReceiveRelayedOpaquePayloadAsync`.
 
 #### A.5.2 Simulated peer -> Main (via relay)
 
 - In the simulator UI, use the simulated peer “send chat to main” action.
 - That path is:
   - `Desktop.Wpf/Features/Simulator/SimulatorStateService.SendChatMessageToMainAsync(...)`
 
 It already:
 
 - builds `ChatEnvelope` inside `InternalEnvelope`
 - encrypts via `EncryptInternalEnvelopeAsync(...)`
 - if `ConnectionMode.ViaRelay`, calls `EnqueueRelayUpstreamToMainAsync(relayHostPeerId, opaqueBytes: cipher.Value, debugType: "Chat", ...)`
 
 **Definition of done (relay):**
 
 - Main can send a chat message to a relayed simulated peer and see it in the simulator card history
 - a relayed simulated peer can send a chat message to Main and see it in Main’s chat history

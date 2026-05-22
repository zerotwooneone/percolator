
## Chunk 0 

This section is the high-level roadmap for delivering end-to-end group chat UX and simulator parity.
It is intentionally ordered by dependency, and it does not assume the current Chunk A/B/C boundaries are final.

End-to-end goals (definition of done):

- Group invites can be sent and received.
- Group invites can be explicitly accepted/declined (approval required).
- Group participants can be modified (add/remove; admin operations as supported).
- Group chat messages can be sent and received (decrypt + persist).
- All of the above works in:
  - Desktop main window UX
  - Simulator

Do not reinvent (guardrails):

- Extend existing query/read-model and WPF selection plumbing rather than creating new parallel UI state stores.
- Reuse existing invite/pending-request UX patterns (connection management dialog) for group invites.
- Reuse structural patterns from the legacy group admin pipeline (command/handler/dispatcher, sequencing/idempotency) while replacing the wire contracts and identity model.
- Keep changes surgical where possible (e.g., replace `ChatReloadCoordinator` repository reads with a read-model query interface instead of rewriting chat state management).

## Chunk 1

Goal:

- Hard-delete the legacy GroupV1 group subsystem and its identity model so GroupV2 can be implemented on a clean slate.

Rules:

- No legacy migration support is required.
- Assume a new SQLite DB file and a regenerated EF Core schema.
- Preserve 1:1 chat features and infrastructure; delete only GroupV1 group assumptions.
- GroupV2 will reintroduce membership/admin persistence later (Chunk H); do not carry forward GroupV1 group tables.

Deliverables:

- Contracts / wire (protobuf):
  - Edit `Percolator.Contracts/Protos/internal_messaging.proto`:
    - Remove legacy group routing fields from non-GroupV2 messages:
      - `TextMessage.group_conversation_guid`
      - `ReadReceipt.group_conversation_guid`
      - `EmojiAnnotation.group_conversation_guid`
      - `DeliveredReceipt.group_conversation_guid`
    - Remove legacy GroupV1 group admin/membership message types and their payloads:
      - `SignedAdminOperation`
      - `UpdateGroupMembershipRequest`
      - `UpdateGroupInfoRequest`
      - any legacy grant/revoke/admin payloads used only by the above.
    - Ensure group identity is unified on `conversation_id` on GroupV2 surfaces.
  - Apply proto edits and remove call sites together:
    - After codegen changes, immediately delete/update all producers/consumers in the same change set so the solution returns to a compiling state.
- Application inbound routing:
  - Update `Percolator.Application/Network/ProcessInternalEnvelopeHandler.cs`:
    - Delete legacy group/admin/membership cases:
      - `ChatEnvelope.MessageOneofCase.SignedAdminOperation`
      - `ChatEnvelope.MessageOneofCase.UpdateGroupMembershipRequest`
      - `ChatEnvelope.MessageOneofCase.UpdateGroupInfoRequest`
    - Delete any group routing branches that depend on legacy group GUID fields.
- Application outbound / command surface:
  - Delete legacy GroupV1 admin and membership write-side surface:
    - `Percolator.Application/Apps/Chat/AdminCommands.cs`
    - `Percolator.Application/Apps/Chat/AdminOperationDispatcher.cs`
    - `Percolator.Application/Apps/Chat/AdminOperationSigner.cs` (if only used for legacy)
    - `Percolator.Application/Apps/Chat/GroupMembershipChangedHandler.cs` (legacy cutover placeholder)
  - Remove any interfaces/types that exist only to support the deleted pipeline.
- Domain + routing identity model:
  - Remove legacy group GUID routing keys:
    - `Percolator.Chat/App/ConversationLookupKey.cs`:
      - remove `GroupConversationGuid` and `ForGroup(Guid ...)`.
  - Remove repository methods that route by legacy group GUID:
    - `Percolator.Chat/IConversationRepository.cs`:
      - remove `GetByGroupGuidAsync(...)`
      - remove `CreateGroupAsync(Guid groupConversationGuid, ...)`
    - `Percolator.Infrastructure/Chat/SqliteConversationRepository.cs`:
      - delete the above implementations.
  - Remove resolver logic that relies on legacy group GUIDs:
    - `Percolator.Infrastructure/Chat/ChatConversationResolver.cs`:
      - delete any lookup paths and filters that depend on `GroupConversationGuid`.
- Persistence + EF Core model reset:
  - Remove legacy group GUID column from persistence model:
    - `Percolator.Infrastructure/Persistence/ConversationDbo.cs`:
      - delete `GroupConversationGuid`.
  - Delete legacy GroupV1 group persistence tables/entities/stores (they encode the old assumptions):
    - `Percolator.Infrastructure/Persistence/GroupMemberDbo.cs`
    - `Percolator.Infrastructure/Persistence/GroupAdminKeyDbo.cs`
    - `Percolator.Infrastructure/Persistence/GroupAdminOpDbo.cs`
    - `Percolator.Infrastructure/Persistence/GroupAdminStateDbo.cs`
    - `Percolator.Infrastructure/Chat/SqliteGroupAdminKeyStore.cs`
    - `Percolator.Infrastructure/Chat/SqliteGroupAdminStateStore.cs`
    - any `SqliteGroupAdminOpStore` / group-manager state store types.
  - Reset EF Core migrations:
    - delete all existing migrations
    - regenerate a new initial migration reflecting the post-GroupV1 schema.
  - Use a new SQLite database file for development/testing.
- Tests:
  - Delete or rewrite tests that depend on GroupV1 group GUID routing and legacy admin/membership messages.
  - Explicitly remove/replace (non-exhaustive):
    - `Percolator.ApplicationIntegrationTests/ChatMessaging/GroupChatEndToEndTests.cs`
    - `Percolator.ApplicationTests/Apps/Chat/GroupAdminHandlersTests.cs`
    - `Percolator.ApplicationTests/Apps/Chat/GroupMembershipChangedHandlerTests.cs`
    - `Percolator.Chat.Tests/AdminOperationsTests.cs`
    - `Percolator.Chat.Tests/UpdateGroupMembershipHandlerTests.cs`
  - Keep the 1:1 chat tests intact.
  - Replace GroupV1 group tests with new GroupV2 tests in `Chunk J`.

Exit criteria:

- Solution compiles.
- 1:1 chat flows compile and run.
- `internal_messaging.proto` contains no legacy GroupV1 admin/membership messages and no `group_conversation_guid` fields.
- There is no remaining reference to `GroupConversationGuid` routing or GroupV1 admin/membership messages.

## Chunk A — Protocol + contract surface

Goal:

- The wire contracts compile and the application layer has a stable, pure-managed crypto boundary for GroupV2.

Deliverables:

- Contracts (protobuf):
  - Confirm these messages are present in `Percolator.Contracts/Protos/internal_messaging.proto`:
    - `ChatEnvelope.create_group` (`CreateGroup`)
    - `ChatEnvelope.group_v2_key_bootstrap` (`GroupV2KeyBootstrap`)
    - `ChatEnvelope.group_v2_message` (`GroupV2Message`)
    - `GroupV2Content` wrapper message used as the plaintext inside group ciphertext.
  - Ensure normal codegen/build updates generated C# contract types.
  - Unify group identity on `conversation_id`:
    - Replace any legacy `group_conversation_guid` fields on group-related payloads with `conversation_id` (GUID bytes).
    - Admin/membership payloads must also reference `conversation_id` (not a separate group GUID).
  - Admin/membership message strategy (GroupV2-only):
    - Represent group admin/membership mutations as encrypted GroupV2 payloads:
      - Add a minimal `oneof` inside `GroupV2Content` (Option A) for:
        - `text_message`
        - `add_member`
        - `remove_member`
      - Minimal field shape (first attempt):
        - `text_message`: existing text string (plus any existing metadata fields already in `GroupV2Content`)
        - `add_member`: target member identity (peer identifier) and any required key material references
        - `remove_member`: target member identity (peer identifier)
      - These operations are carried inside `ChatEnvelope.group_v2_message` and are keyed/routed by `conversation_id`.
    - Remove legacy admin/membership message types once replaced (see `Chunk 0.K`).
- Crypto boundary (application-facing):
  - Use `Percolator.Cryptography.IGroupCryptographyService` for:
    - generating/rehydrating a `GroupMasterKey`
    - deriving `GroupId` and `BlobKey`
  - Use `Percolator.Cryptography.IGroupV2MessageCryptographyService` for:
    - encrypting/decrypting `GroupV2Content` payloads
  - Keep native/FFI (zkgroup) confined to `Percolator.Infrastructure`.
- Inbound routing skeleton (no business logic yet):
  - Update `Percolator.Application/Network/ProcessInternalEnvelopeHandler.cs` to include switch cases for:
    - `ChatEnvelope.MessageOneofCase.GroupV2KeyBootstrap`
    - `ChatEnvelope.MessageOneofCase.GroupV2Message`

## Chunk B — Persistence + invariants (GroupMasterKey)

Goal:

- The application can reliably read/write raw 32-byte `GroupMasterKeyBytes` keyed by `ConversationId`.

Deliverables:

- Confirm persistence foundations exist:
  - `Percolator.Infrastructure/Persistence/GroupCryptoStateDbo.cs`
  - migration `*RenameEncryptedGroupMasterKeyBytesToGroupMasterKeyBytes*`
- Implement the infrastructure repository for the existing contract:
  - `Percolator.Chat.App.IGroupCryptoStateRepository`
  - EF-backed implementation that reads/writes `GroupCryptoStateDbo.GroupMasterKeyBytes`.
- DI registration:
  - Register `IGroupCryptoStateRepository` so application handlers can resolve it.
- Invariants:
  - On read: validate `GroupMasterKeyBytes.Length == 32`, otherwise throw.
  - On write: only accept/write exactly 32 bytes.
- Security posture:
  - No column-level encryption for `GroupMasterKeyBytes`.
  - Rely on encrypted SQLite file.

## Chunk C — Group invite + acceptance semantics

Goal:

- Group creation and invite receipt require explicit acceptance and have consistent persistence effects.

Deliverables:

- Confirm current delivery path:
  - Creator sends `ChatEnvelope.create_group` to each participant (see `CreateGroupFromIdentityKeysHandler`).
  - Recipient processes inbound `ChatEnvelope.create_group` in `ProcessInternalEnvelopeHandler` and dispatches `CreateGroupFromIdentityKeysCommand`.
- Approval required (locked semantic):
  - On receiving `CreateGroup`, persist a pending group invitation.
  - Add application commands/handlers:
    - `AcceptGroupInvite`
    - `DeclineGroupInvite`
  - Acceptance effects:
    - create the group `Conversation` (keyed by `ConversationId`) and its group extension/attributes
    - create membership rows
    - enforce bootstrap gating (must not enable send/decrypt until bootstrap exists).
  - Decline effects:
    - remove the pending invite record (and do not create a conversation).

UX rule (existing behavior; do not redesign):

- If the local main-window user initiated group creation, the group conversation shows up in the main window list of conversations immediately.
- If another peer initiated the group conversation, it is surfaced as a pending request in the connection management dialog until the user accepts.

Do not reinvent (extension points):

- Pending group invitations should reuse the existing connection management dialog request pattern rather than introducing a new invite UI surface.
- Where possible, model group invites similarly to existing pending invitation persistence and acceptance flows.

## Chunk D — Key bootstrap handling (GroupMasterKey distribution)

Goal:

- Group members become able to decrypt/send group messages only after receiving and persisting a `GroupV2KeyBootstrap`.

Deliverables:

- Outbound bootstrap (creator/admin path):
  - In `CreateGroupFromIdentityKeysHandler`:
    - generate a new `GroupMasterKey`
    - persist it via `IGroupCryptoStateRepository`
    - send `ChatEnvelope.group_v2_key_bootstrap` to each recipient via the existing 1:1 tunnels.
- Inbound bootstrap handling:
  - Add `ChatEnvelope.MessageOneofCase.GroupV2KeyBootstrap` handling in `ProcessInternalEnvelopeHandler`:
    - validate `conversation_id` is exactly 16 bytes and is not `Guid.Empty`
    - validate `group_master_key_bytes` is exactly 32 bytes
    - persist via `IGroupCryptoStateRepository.UpsertGroupMasterKeyAsync(...)`.
- Ordering rule:
  - Receipt of `CreateGroup` (keyed by `conversation_id`) must create the conversation + membership before the UI can show the group.
  - Receipt of `GroupV2KeyBootstrap` enables send/decrypt.
- Missing-state rule:
  - If a `GroupV2Message` arrives and no stored `GroupMasterKey` exists for the conversation:
    - do not persist plaintext
    - surface a clear error/log (expected until bootstrap is received).

## Chunk E — Group messaging vertical slice (send/receive)

Goal:

- A plaintext message can be sent to a group and received/decrypted/persisted on recipients.

Deliverables:

- Outbound group send command/handler:
  - Input: `ConversationId` + plaintext string.
  - Resolve member recipients via query/read-model interfaces (not repositories).
    - Add/extend a query interface method to support fanout recipient resolution by `ConversationId`.
    - Minimum contract shape:
      - Input: `ConversationId`
      - Output: list of recipients (at minimum `PeerId` / participant GUIDs; plus any routing hints if required by the sender).
  - Load `GroupMasterKey` via `IGroupCryptoStateRepository`.
  - Derive `GroupId` + `BlobKey` via `IGroupCryptographyService`.
  - Serialize `GroupV2Content` and encrypt via `IGroupV2MessageCryptographyService`.
  - Send `ChatEnvelope.group_v2_message` via `IRemoteEnvelopeSender` to each recipient.
- Send preconditions (enforced in handler):
  - conversation must be a group conversation
  - member list must be non-empty
  - `GroupMasterKey` must exist locally.
- Inbound group receive handling:
  - Add `ChatEnvelope.MessageOneofCase.GroupV2Message` case in `ProcessInternalEnvelopeHandler`:
    - convert `conversation_id` bytes -> `ConversationId` (reject empty)
    - load `GroupMasterKey` via `IGroupCryptoStateRepository`
    - derive `GroupId` + `BlobKey`
    - decrypt ciphertext -> `GroupV2Content`
    - persist `GroupV2Content.text_message` into the existing message pipeline.

## Chunk F — Read models / query surfaces for WPF UI

Goal:

- WPF can render sidebar + message history + group details using query/read-model interfaces only.

Deliverables:

- Sidebar query surface (extend existing):
  - WPF uses `Percolator.Application.Sessions.IPeerConnectionSidebarQueries.LoadSidebarConnectionsAsync(...)` via `Desktop.Wpf.Features.Sessions.PeerConnectionStateService`.
  - Implementation: `Percolator.Infrastructure.Sessions.PeerConnectionSidebarQueries`.
  - Extend contract + implementation to return group items as first-class selectable entries:
    - add new `SidebarPeerConnectionKeyType` variant for groups (e.g., `GroupConversation`)
    - for groups, `KeyValue` is the `ConversationId` (GUID) (unified identity).
  - Update:
    - `PeerConnectionStateService` mapping (group items produce a `PeerConnectionKey`)
    - `SessionsSidebarViewModel.TryParseKey(...)` to parse the new group key type.
- Message history query (replace repository reads):
  - `Desktop.Wpf.Features.Chat.ChatReloadCoordinator` currently reads via `Percolator.Chat.App.IConversationRepository`.
  - Replace with a read-model query interface and infrastructure implementation (example contract name):
    - `Percolator.Application.Chat.IConversationMessageQueries`
  - Must load messages by `ConversationId` for both direct + group.
  - Update `ChatReloadCoordinator` to use the query interface.

Do not reinvent (surgical change):

- Replace only the read-side message loading in `ChatReloadCoordinator`; keep the existing debounce/trigger/state sync mechanisms.
- Group details snapshot query for admin UI:
  - Add a read-model query interface returning a snapshot DTO:
    - group name
    - members
    - admins
    - "am I admin" capability.
- Rule (enforced):
  - WPF reads must not call domain repositories.

UX rules (existing behavior; do not redesign):

- Pending group invitations are handled via the connection management dialog (not the main sidebar list).
- When a conversation has not been bootstrapped yet, use the existing "in progress" view/viewmodel that is used for 1:1 sessions before the X3DH session has been initiated.

Unified identity + schema rule:

- `ConversationId` is the only identifier for direct and group conversations.
- Group is represented as an optional extension/attribute keyed by `ConversationId`:
  - Replace `ConversationDbo.GroupConversationGuid` with a group extension table (1:1) keyed by `ConversationId` (recommended), or an explicit `Conversation.Kind`.
  - All wire contracts use `conversation_id` for group routing and operations.

Do not reinvent (migration note):

- The codebase currently has legacy group routing identities (`GroupConversationGuid`, `ConversationLookupKey.ForGroup(...)`, repository methods routing by group GUID).
- This must be migrated early because it affects inbound routing, persistence, UI selection keys, and admin/membership operations.

## Chunk G — Desktop main window UX (selection + chat)

Goal:

- Group conversations appear/select like direct chats, and the main pane can load + send group messages.

Deliverables (suggested approach; confirm desired UX before implementing):

- Selection plumbing (suggestion):
  - Selection is stored in `Desktop.Wpf.Features.Sessions.State.SelectedChannelModel.SelectedKey`.
  - Extend selection to include a group key type and propagate through:
    - `PeerConnectionStateService` (key construction)
    - `SessionsSidebarViewModel` (key parsing)
    - `SelectedChannelPaneViewModel` (content resolution).
- Message pane behavior (suggestion):
  - On group selection:
    - resolve `ConversationId`
    - load messages via the message-history query interface (Chunk 0.F).
  - Send button:
    - routes to the outbound group send command/handler (Chunk 0.E)
    - does not use `PostTextMessageCommand` for groups.

UI open questions (confirm before implementation):

- Should groups share the exact same `ChatViewModel` as direct sessions, or have a separate view model?
- Should the main window show group metadata (name/members) inline above the chat, or in a separate pane/dialog?

## Chunk H — Group admin + membership changes

Goal:

- Admin/membership operations exist as write-side commands/handlers and the UI can drive them and refresh via read models.

Deliverables:

- GroupV2-only rule:
  - After this plan is implemented, GroupV2 is the only supported form of group messaging and group administration.
  - Any legacy GroupV1 group messaging/admin code paths that become dead must be removed (see `Chunk 0.K`).
- Identity rule:
  - Admin/membership operations reference `conversation_id` (GUID bytes) for group operations.
  - Remove/replace any use of legacy `group_conversation_guid` as an identifier.
- Implement write-side operations required by UI:
  - MVP (per session flow):
    - add member:
      - must deliver `GroupV2KeyBootstrap` to the new member
    - remove member (cryptographic eviction):
      - must rotate `GroupMasterKey` (new epoch)
      - must deliver new bootstrap to remaining members
      - must exclude the removed member from the bootstrap fanout
  - Deferred (explicitly not required for MVP unless you decide otherwise):
    - rename group
    - grant/revoke admin
- Wire + handler rule (GroupV2-only):
  - These operations are sent as encrypted `GroupV2Content` payload variants inside `ChatEnvelope.group_v2_message`.
  - Inbound handling for these variants must:
    - validate sender is authorized for the operation (based on persisted group state)
    - apply persistence effects (membership/role/name changes)
    - trigger any required key rotation + re-bootstrap fanout.

Do not reinvent (reuse legacy structure):

- Reuse the existing command/handler/dispatcher shape from the legacy GroupV1 admin pipeline where it still fits:
  - per-operation MediatR handlers
  - centralized dispatch/fanout to recipients
  - sequencing/idempotency patterns
- Replace the wire contracts and identity model (move from legacy admin messages and group GUID routing to encrypted GroupV2 payloads keyed by `ConversationId`).

Persistence model (Signal-inspired; local state):

- Group configuration state is treated as a sensitive state machine that must be persisted and updated as membership/admin operations occur.
- Add/extend persistence keyed by `ConversationId`:
  - Group state (1:1 extension):
    - `ConversationId`
    - epoch number (monotonic integer)
    - optional group display name (if supported)
  - Group members:
    - `ConversationId`
    - member identifier
    - role (at minimum: member/admin)
    - joined/removed timestamps (or status)

Epoch cutover constraint (remove member):

- During removal/key rotation, outbound group sends must be blocked until the new `GroupMasterKey` has been distributed to all remaining members.
- Group messages encrypted under an old epoch must not be sent once the removal flow begins.
- UI behavior (reactive pattern):
  - expose commands as `AsyncRelayCommand`s
  - show progress/errors via reactive properties
  - prefer reload-from-source after success.
- After any mutation:
  - refresh sidebar preview + group details + message pane via query/read models (Chunk 0.F).

UI open questions (confirm before implementation):

- Should the MVP include group rename, or should it be deferred until after membership add/remove is working end-to-end?

## Chunk I — Simulator parity gate

Goal:

- Simulator can drive and validate the same group flows as the main app.

Deliverables:

- Extend simulator plumbing to cover:
  - create group (via `CreateGroupFromIdentityKeysCommand` / envelope)
  - receive/process `ChatEnvelope.create_group`
  - receive/process `ChatEnvelope.group_v2_key_bootstrap`
  - send/receive `ChatEnvelope.group_v2_message`.
- Update simulator UI and outbound interception so it is not hard-coded to direct `TextMessage`:
  - `Desktop.Wpf/Features/Simulator/SimulatorChatViewModel.cs`
  - `Desktop.Wpf/Features/Simulator/SimulatorOutboundInterceptor.cs`.

## Chunk J — Tests + cleanup gates

Goal:

- Group V2 end-to-end behavior is covered by tests, and legacy code paths are removed.

Deliverables:

- Integration path test:
  - create group -> bootstrap delivered -> send `GroupV2Message` -> decrypt on recipient -> plaintext persisted.
- Negative test:
  - `GroupV2Message` before bootstrap does not persist plaintext.
- Cleanup gate:
  - Dead code removal must be complete (see `Chunk 0.K`) before considering the rollout finished.

## Chunk K — Dead code removal (GroupV1)

Goal:

- Remove all GroupV1 group messaging/admin code paths and contracts that are no longer used once GroupV2 is implemented.

Deliverables (explicit deletion checklist):

- Contracts / wire:
  - Remove any legacy group routing fields on non-GroupV2 messages:
    - `TextMessage.group_conversation_guid`
    - `ReadReceipt.group_conversation_guid`
    - `EmojiAnnotation.group_conversation_guid`
    - `DeliveredReceipt.group_conversation_guid`
  - Remove legacy group admin/membership message types that are superseded by GroupV2 operations:
    - `SignedAdminOperation`
    - `UpdateGroupMembershipRequest`
    - `UpdateGroupInfoRequest`
- Application inbound handling:
  - Delete the corresponding cases from [Percolator.Application/Network/ProcessInternalEnvelopeHandler.cs](cci:7://file:///C:/Users/squir/source/repos/percolator/source/Percolator.Application/Network/ProcessInternalEnvelopeHandler.cs:0:0-0:0):
    - `ChatEnvelope.MessageOneofCase.SignedAdminOperation`
    - `ChatEnvelope.MessageOneofCase.UpdateGroupMembershipRequest`
    - `ChatEnvelope.MessageOneofCase.UpdateGroupInfoRequest`
    - any `TextMessage` group-routing branch that depends on legacy group GUID routing.
- Application outbound/commands:
  - Remove legacy GroupV1 admin command surface and dispatch plumbing:
    - [Percolator.Application/Apps/Chat/AdminCommands.cs](cci:7://file:///C:/Users/squir/source/repos/percolator/source/Percolator.Application/Apps/Chat/AdminCommands.cs:0:0-0:0)
    - [Percolator.Application/Apps/Chat/AdminOperationDispatcher.cs](cci:7://file:///C:/Users/squir/source/repos/percolator/source/Percolator.Application/Apps/Chat/AdminOperationDispatcher.cs:0:0-0:0)
    - [Percolator.Application/Apps/Chat/AdminOperationSigner.cs](cci:7://file:///C:/Users/squir/source/repos/percolator/source/Percolator.Application/Apps/Chat/AdminOperationSigner.cs:0:0-0:0) (if only used by legacy admin ops)
    - any `UpdateGroupMembershipCommand` / `UpdateGroupInfoCommand` pipeline that exists only for legacy messages.
- Persistence / model:
  - Remove `ConversationDbo.GroupConversationGuid` and any repository methods that route by it:
    - [IConversationRepository.GetByGroupGuidAsync(...)](cci:1://file:///C:/Users/squir/source/repos/percolator/source/Percolator.Infrastructure/Chat/SqliteConversationRepository.cs:118:4-127:5)
    - [IConversationRepository.CreateGroupAsync(Guid groupConversationGuid, ...)](cci:1://file:///C:/Users/squir/source/repos/percolator/source/Percolator.Infrastructure/Chat/SqliteConversationRepository.cs:16:4-48:5)
  - Remove any [ConversationLookupKey.ForGroup(...)](cci:1://file:///C:/Users/squir/source/repos/percolator/source/Percolator.Chat/App/ConversationLookupKey.cs:18:4-18:95) / group GUID routing shape if it only exists for GroupV1.
- WPF / simulator:
  - Remove any UI paths that assume group messages arrive as `ChatEnvelope.text_message` with a group GUID.
  - Ensure simulator is not intercepting or generating legacy GroupV1 group message/admin envelopes.


## Rules of Execution for AI Agents

1. **File Locations & Namespaces:** 
   - Persistence (DBOs): `Percolator.Infrastructure/Chat/Persistence`
   - Commands/Handlers: `Percolator.Application/Apps/Chat`
   - Interfaces/Queries (Contracts): `Percolator.Application/Chat`
   - Query Implementations: `Percolator.Infrastructure/Chat`
2. **EF Migrations:** To generate migrations, use PowerShell and run: `dotnet ef migrations add <MigrationName> --project source\Percolator.Infrastructure --startup-project source\Percolator.Node`
3. **WPF UI Targets:** 
   - Group Action Menu (above chat): Modify `Desktop.Wpf/Features/Chat/ChatView.xaml`.
   - "Create Group" Tab & Multi-select: Modify `Desktop.Wpf/Features/Sessions/ConnectionManagementDialogWindow.xaml` and `ConnectionManagementDialogViewModel.cs`.
   - Pending Invites Display: Also modify `ConnectionManagementDialogWindow.xaml` / `ConnectionManagementDialogViewModel.cs`.
   - Sidebar UI Updates: Modify `Desktop.Wpf/Features/Sessions/SessionsSidebarView.xaml`.
4. **Dialogs:** To open new dialogs (e.g., for picking a member to add), do NOT create raw `Window` instances directly in ViewModels. Instead, use `IWindowManager.ShowFor<YourNewViewModel>()` matching the pattern seen in `PendingHandshakesMenuViewModel.cs`. You will need to create the View/ViewModel pair and map them in `Desktop.Wpf/Shared/Windowing/ViewMappings.xaml`.

---

## Chunk 0  (Complete)

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

## Architecture Overview

This implementation uses **Signal-inspired P2P groups** - it adopts Signal Group V2's cryptographic primitives and key derivation patterns but operates in a peer-to-peer environment without Signal's server infrastructure.

**Key differences from Signal Group V2:**
- **No server component** - groups are fully peer-to-peer
- **No Zero-Knowledge proofs** - membership is based on possession of GroupMasterKey
- **Hybrid delivery** - messages sent via relay OR direct 1:1 tunnels
- **No identity obfuscation** - member identities are visible within group context (encrypted at rest)
- **O(n) fanout** - sender sends to each unique relay + direct peer

**Security posture:**
- Cryptographic security: Uses Signal's GroupMasterKey derivation and encryption
- Privacy: Content encrypted, but IP addresses visible in direct delivery
- Access control: Based on possession of GroupMasterKey (no per-operation ZK proofs)
- At-rest security: Database encrypted (assumed secure)

**Delivery model:**
- Senders use existing route table to determine delivery path per peer
- Messages sent to each unique relay + each direct peer
- Recipient doesn't care about delivery path - same protocol regardless
- User may someday select preferred route (for now: use first route in table)

## Chunk 1 (Complete)

Goal:

- Hard-delete the legacy groupV1 subsystem and its identity model so the new group system can be implemented on a clean slate.

Rules:

- No legacy migration support is required.
- Assume a new SQLite DB file and a regenerated EF Core schema.
- Preserve 1:1 chat features and infrastructure; delete only GroupV1 group assumptions.
- The new group system will reintroduce membership/admin persistence later (Chunk G); do not carry forward legacy group tables.

---

## Chunk A — Protocol + contract surface

Goal:

- The wire contracts compile and the application layer has a stable, pure-managed crypto boundary for the Signal-based group system.

Deliverables:

- Contracts (protobuf):
  - Add these messages to `Percolator.Contracts/Protos/internal_messaging.proto`:
    - `ChatEnvelope.create_group` (`CreateGroup`) - initial group invitation
    - `ChatEnvelope.group_key_bootstrap` (`GroupKeyBootstrap`) - master key distribution
    - `ChatEnvelope.group_message` (`GroupMessage`) - encrypted group content
    - `GroupContent` wrapper message used as the plaintext inside group ciphertext
  - Note: These are new message types within the existing `ChatEnvelope`, NOT new gRPC methods
  - Note: `ChatEnvelope` is carried inside `InternalEnvelope`, which is encrypted via existing secure sessions (X3DH double ratchet)
  - Requirement: Each peer added to a group must already have an existing secure session with the group creator
  - Feasibility: Group messages are sent over existing secure sessions using the existing envelope infrastructure - no new gRPC methods needed
  - Protobuf field definitions:
    - Note: Messages already exist in `internal_messaging.proto` as `CreateGroup`, `GroupKeyBootstrap`, `GroupMessage`, `GroupContent`
    - `CreateGroup`: `conversation_id` (bytes), `creator_identity_key` (bytes), `initial_participant_identity_keys` (repeated bytes), `name` (string, optional)
    - `GroupKeyBootstrap`: `conversation_id` (bytes), `group_master_key_bytes` (bytes)
    - `GroupMessage`: `message_id` (bytes), `author_identity_key` (bytes), `sent_timestamp_utc` (timestamp), `conversation_id` (bytes), `group_id` (bytes), `ciphertext` (bytes), `epoch` (uint32)
    - `GroupContent`: `text_message` (string)
  - Note: Protobuf "V2" naming has been removed from `internal_messaging.proto` (GroupV2KeyBootstrap → GroupKeyBootstrap, etc.)
  - Ensure normal codegen/build updates generated C# contract types
  - Unified group identity on `conversation_id`:
    - All group-related payloads use `conversation_id` (GUID bytes) as the group identifier
    - No separate group GUID field; `ConversationId` is the sole identity
  - Group content message structure (Signal protocol):
    - Note: `GroupContent` currently only has `text_message` field
    - Future: Extend `GroupContent` with `oneof` for `add_member`, `remove_member`, `change_title` when implementing Chunk G
    - Current implementation: Only text messages in Chunk D, membership operations deferred to Chunk G
- Crypto boundary (application-facing):
  - Create `Percolator.Cryptography.IGroupCryptographyService` interface:
    - `byte[] GenerateGroupMasterKey()` - generate 32-byte random key
    - `byte[] DeriveGroupId(byte[] groupMasterKey)` - derive GroupId via KDF
    - `byte[] DeriveBlobKey(byte[] groupMasterKey)` - derive BlobKey via KDF
  - Create `Percolator.Cryptography.IGroupMessageCryptographyService` interface:
    - `byte[] EncryptGroupContent(byte[] blobKey, GroupContent content)` - encrypt content
    - `GroupContent DecryptGroupContent(byte[] blobKey, byte[] ciphertext)` - decrypt content
  - Keep native/FFI (zkgroup) confined to `Percolator.Infrastructure`
- Identity model (Signal-based security):
  - `ConversationId` (GUID) = application/database identifier for routing and persistence
  - `GroupId` (derived from `GroupMasterKey`) = cryptographic identifier for encryption/decryption
  - `BlobKey` (derived from `GroupMasterKey`) = symmetric key for message encryption (derived on-demand)
  - Cryptographic binding: same master key always derives to same GroupId and BlobKey
  - Store only `GroupMasterKeyBytes` in `GroupCryptoStateDbo` (derive GroupId and BlobKey on-demand)
  - Use `ConversationId` for all application-level operations
  - Use `GroupId` only for cryptographic operations
  - Group metadata (name, membership) stored in plaintext (protected by database encryption)
- Inbound routing skeleton (no business logic yet):
  - Update `Percolator.Application/Network/ProcessInternalEnvelopeHandler.cs` to include switch cases for:
    - `ChatEnvelope.MessageOneofCase.CreateGroup` (add skeleton, business logic in Chunk C)
    - `ChatEnvelope.MessageOneofCase.GroupKeyBootstrap` (add skeleton, business logic in Chunk C)
    - `ChatEnvelope.MessageOneofCase.GroupMessage` (add skeleton, business logic in Chunk D)
  - Note: `IRemoteEnvelopeSender` and `IMessageService` require no changes - they already accept any `ChatEnvelope` type

## Chunk B — Persistence + invariants (GroupMasterKey)

Goal:

- The application can reliably read/write raw 32-byte `GroupMasterKeyBytes` keyed by `ConversationId`.

Deliverables:

- Create persistence foundation:
  - `Percolator.Infrastructure/Persistence/GroupCryptoStateDbo.cs`:
    - `ConversationId` (GUID, primary key)
    - `GroupMasterKeyBytes` (byte[], exactly 32 bytes)
    - `CreatedAtUtc` (timestamp)
    - `UpdatedAtUtc` (timestamp)
  - Add EF Core migration to create the `GroupCryptoStates` table (`dotnet ef migrations add ...`)
  - Note: GroupId and BlobKey derived on-demand from GroupMasterKey (not persisted)
- Implement the repository interface:
  - `Percolator.Chat.App.IGroupCryptoStateRepository`:
    - `Task UpsertGroupMasterKeyAsync(Guid conversationId, byte[] groupMasterKeyBytes, CancellationToken cancellationToken)`
    - `Task<byte[]?> GetGroupMasterKeyAsync(Guid conversationId, CancellationToken cancellationToken)`
    - `Task DeleteGroupMasterKeyAsync(Guid conversationId, CancellationToken cancellationToken)`
  - EF-backed implementation in `Percolator.Infrastructure.Chat.SqliteGroupCryptoStateRepository`
- DI registration:
  - Register `IGroupCryptoStateRepository` in `Percolator.Infrastructure.Chat.ServiceCollectionExtensions.AddChatInfrastructure`
- Invariants:
  - On read: validate `GroupMasterKeyBytes.Length == 32`, otherwise throw
  - On write: only accept exactly 32 bytes, otherwise throw
  - Upsert semantics: insert if not exists, update if exists
- Security posture:
  - No column-level encryption for `GroupMasterKeyBytes`
  - Rely on encrypted SQLite file

## Chunk C — Group invite + acceptance semantics (includes bootstrap)

Goal:

- Group creation and invite receipt require explicit acceptance and have consistent persistence effects.
- Group bootstrap (master key distribution) is part of group creation flow.

### C.1: Database Extensions & Conversation Models
- Conversation kind extension (moved from Chunk E):
  - Add `ConversationKind` enum to `Percolator.Chat`:
    - `Direct` - 1:1 conversation
    - `Group` - group conversation
  - Add `Kind` property to `ConversationDbo` (enum, indexed)
  - EF Core migration to add column (`dotnet ef migrations add ...`)
- Group membership persistence (moved from Chunk G):
  - `GroupMemberDbo` in `Percolator.Infrastructure/Chat/Persistence`:
    - `ConversationId` (GUID, foreign key)
    - `PeerId` (GUID, composite key with ConversationId)
    - `Role` (enum: Member, Admin)
    - `JoinedAtUtc` (timestamp)
    - `RemovedAtUtc` (nullable timestamp)
  - EF Core migration to create table (`dotnet ef migrations add ...`)
  - Index on `ConversationId` for efficient queries
  - Note: Delivery path determined from existing route table (not stored here)
- Group state persistence (moved from Chunk G):
  - `GroupStateDbo` in `Percolator.Infrastructure/Chat/Persistence`:
    - `ConversationId` (GUID, primary key)
    - `Epoch` (integer, monotonic)
    - `Name` (string, nullable)
    - `CreatedAtUtc` (timestamp)
    - `UpdatedAtUtc` (timestamp)
  - EF Core migration to create table (`dotnet ef migrations add ...`)
  - Note: Group name stored in plaintext (protected by database encryption)
- Persistence for pending invitations:
  - `PendingGroupInvitationDbo` in `Percolator.Infrastructure/Chat/Persistence`:
    - `Id` (GUID, primary key)
    - `ConversationId` (GUID, indexed)
    - `InviterPeerId` (GUID)
    - `CreatorIdentityKey` (byte[])
    - `InitialMembers` (serialized list of byte[])
    - `GroupName` (string, nullable)
    - `ReceivedAtUtc` (timestamp)
    - `Status` (enum: Pending, Accepted, Declined)
  - EF Core migration to create table (`dotnet ef migrations add ...`)

### C.2: Outbound Group Creation (Commands)
- Create outbound group creation command/handler:
  - `Percolator.Application.Apps.Chat.CreateGroupCommand`:
    - Input: `SelfIdentityId`, `List<PeerId>` initial members, optional group name
  - Handler: `CreateGroupCommandHandler`:
    - Validate that secure sessions exist with all initial members (via `IDirectSessionRepository`)
    - Generate a new `ConversationId` (GUID)
    - Generate a new `GroupMasterKey` (32 random bytes) via `IGroupCryptographyService`
    - Derive `GroupId` from master key via KDF
    - Persist `GroupMasterKey` via `IGroupCryptoStateRepository.UpsertGroupMasterKeyAsync`
    - Create `ChatEnvelope.create_group` with:
      - `conversation_id` (GUID bytes)
      - `creator_identity_key` (SPKI bytes)
      - `initial_participant_identity_keys` (list of SPKI bytes)
      - optional `name`
    - Send to each initial member via `IRemoteEnvelopeSender.SendChatEnvelopeToPeerAsync`:
      - Construct `RecipientRoute` for each member: `new RecipientRoute(peerId, publicKeyHash)`
      - Resolve `publicKeyHash` via `IPeerPublicSigningKeyStore.GetPublicKeyHashByPeerIdAsync(peerId)`
      - Pattern follows existing `DispatchTextMessageHandler`
    - Create `ChatEnvelope.group_key_bootstrap` with:
      - `conversation_id` (GUID bytes)
      - `group_master_key_bytes` (32 bytes)
    - Send to each initial member via `IRemoteEnvelopeSender.SendChatEnvelopeToPeerAsync`
    - Create local `Conversation` record (kind = group) immediately for creator
    - Create local `GroupStateDbo` (epoch = 0, creator as admin)
    - Create local membership records for all members (creator = Admin, others = Member)

### C.3: Inbound Routing & Handlers
- Inbound group invitation handling:
  - Add `ChatEnvelope.MessageOneofCase.CreateGroup` case in `ProcessInternalEnvelopeHandler`:
    - Validate `conversation_id` is 16 bytes and not empty
    - Validate `initial_participant_identity_keys` is not empty
    - Persist pending group invitation to `PendingGroupInvitationDbo`
    - Dispatch notification for UI to surface in connection management dialog
- Inbound bootstrap handling (merged from Chunk D):
  - Add `ChatEnvelope.MessageOneofCase.GroupKeyBootstrap` case in `ProcessInternalEnvelopeHandler`:
    - Validate `conversation_id` is exactly 16 bytes and not `Guid.Empty`
    - Validate `group_master_key_bytes` is exactly 32 bytes
    - Derive `GroupId` from master key via KDF
    - Persist via `IGroupCryptoStateRepository.UpsertGroupMasterKeyAsync`
    - Log successful bootstrap receipt

### C.4: Accept/Decline & Queries
- Acceptance/decline commands (`Percolator.Application.Apps.Chat`):
  - `AcceptGroupInviteCommand`:
    - Input: `ConversationId`, `SelfIdentityId`
    - Create `Conversation` record (kind = group)
    - Create `GroupStateDbo` (epoch = 0, pending bootstrap)
    - Create membership records (self = Member)
    - Update `PendingGroupInvitationDbo.Status` to Accepted
  - `DeclineGroupInviteCommand`:
    - Input: `ConversationId`, `SelfIdentityId`
    - Update `PendingGroupInvitationDbo.Status` to Declined
    - Do not create conversation or membership records
- Query interface for pending invitations:
  - `IPendingGroupInvitationQueries` in `Percolator.Application.Chat`:
    - `Task<List<PendingGroupInvitationDto>> GetPendingInvitationsAsync(CancellationToken cancellationToken)`
  - `PendingGroupInvitationDto`:
    - `ConversationId` (GUID)
    - `InviterPeerId` (GUID)
    - `CreatorIdentityKey` (byte[])
    - `InitialMembers` (list of byte[])
    - `GroupName` (string, nullable)
    - `ReceivedAtUtc` (timestamp)
  - Implementation `SqlitePendingGroupInvitationQueries` in `Percolator.Infrastructure/Chat/Queries`

### Domain Rules (Apply to handlers)
- Domain rules for admin status:
  - Creator is always assigned Admin role when group is created
  - Domain enforces at least 1 admin remains in group (prevent removing last admin)
  - Admin role cannot be removed, only transferred (future enhancement)

UX rule (existing behavior; do not redesign):

- **Initiating a group**: If the local user initiated group creation, the group conversation shows up in the main window list immediately.
- **Receiving an invite**: If a different (remote or simulated) peer requests the main window to join a group, that request appears in the `PendingInvitations` list of the connection management dialog. The list item should display context like *"Alice invited you to a group: [Name]"*.

Do not reinvent (extension points):

- Pending group invitations reuse the existing connection management dialog request pattern
- Model group invites similarly to existing pending invitation persistence and acceptance flows

## Chunk D — Group messaging vertical slice (send/receive)

Goal:

- A plaintext message can be sent to a group and received/decrypted/persisted on recipients.

Deliverables:
- Outbound group send command/handler:
  - `Percolator.Application.Apps.Chat.SendGroupMessageCommand`:
    - Input: `ConversationId`, `MessageId`, `Content`, `SentTimestampUtc`, `SelfIdentityId`
  - Handler: `SendGroupMessageCommandHandler`:
    - Load conversation via repository to verify it's a group
    - Resolve member recipients via query/read-model interface (not repository):
      - Add `IConversationMemberQueries.GetGroupMembersWithRoutesAsync(Guid conversationId)`
      - Returns list of `GroupMemberWithRouteDto` including delivery path info
      - Implementation uses `IPeerRoutingProfileRepository` + `IProfileRoutePlanner` to determine delivery path per peer:
        - Load `PeerRoutingProfile` for each member
        - Call `IProfileRoutePlanner.SelectRoute(profile)` to get `RouteSelection`
        - If `RouteSelection.Relay` is null → direct delivery
        - If `RouteSelection.Relay` is not null → relay delivery via that relay peer
    - Load `GroupMasterKey` via `IGroupCryptoStateRepository`
    - Derive `GroupId` + `BlobKey` via `IGroupCryptographyService`
    - Create `GroupContent` and set `text_message` field directly (not a oneof yet)
    - Encrypt via `IGroupMessageCryptographyService`
    - Hybrid delivery:
      - Group members by delivery path (direct vs relay)
      - Send to each direct peer via `IRemoteEnvelopeSender.SendChatEnvelopeToPeerAsync`
      - Send to each unique relay peer via `IRemoteEnvelopeSender.SendChatEnvelopeToPeerAsync` (relay fans out to its members)
      - Note: Routing is automatic - `DefaultNetworkSender` uses `IProfileRoutePlanner` to select direct/relay based on peer profile
    - Persist message locally to `MessageDbo` (sender's copy)
- Send preconditions (enforced in handler):
  - Conversation must exist and have `Kind == Group`
  - Member list must be non-empty
  - `GroupMasterKey` must exist locally (throw if not)
- Inbound group receive handling:
  - Add `ChatEnvelope.MessageOneofCase.GroupMessage` case in `ProcessInternalEnvelopeHandler`:
    - Convert `conversation_id` bytes -> `ConversationId` (reject empty)
    - Load `GroupMasterKey` via `IGroupCryptoStateRepository`
    - If no master key exists, log warning and return (do not process)
    - Derive `GroupId` + `BlobKey`
    - Decrypt ciphertext -> `GroupContent`
    - Switch on `GroupContent` fields:
      - `text_message`: persist to `MessageDbo` via existing message writer
      - Future: `add_member`, `remove_member`, `change_title` when Chunk G extends protobuf
- Message persistence:
  - Reuse existing `MessageDbo` structure (no changes needed)
  - Note: GroupId not stored in MessageDbo (cryptographic context only, not needed for queries)

## Chunk E — Read models / query surfaces for WPF UI

Goal:

- WPF can render sidebar + message history + group details using query/read-model interfaces only.

Deliverables:

- Sidebar query surface (extend existing):
  - WPF uses `Percolator.Application.Sessions.IPeerConnectionSidebarQueries.LoadSidebarConnectionsAsync(...)` via `Desktop.Wpf.Features.Sessions.PeerConnectionStateService`
  - Implementation: `Percolator.Infrastructure.Sessions.PeerConnectionSidebarQueries`
  - Extend contract + implementation to return group items as first-class selectable entries:
    - Add new `SidebarConnectionType` variant for groups (e.g., `GroupConversation`)
    - For groups, `KeyValue` is the `ConversationId` (GUID) - unified identity
    - Query logic: Filter `ConversationDbo` by `Kind == Group` and join with `GroupStateDbo` for group name
  - Update:
    - `PeerConnectionStateService` mapping (group items produce a `PeerConnectionKey`)
    - `SessionsSidebarViewModel.TryParseKey(...)` to parse the new group key type
- Message history query (replace repository reads):
  - `Desktop.Wpf.Features.Chat.ChatReloadCoordinator` currently reads via `Percolator.Chat.App.IConversationRepository`
  - Replace with read-model query interface and infrastructure implementation:
    - `Percolator.Application.Chat.IConversationMessageQueries`:
      - `Task<List<MessageDto>> GetMessagesAsync(Guid conversationId, CancellationToken cancellationToken)`
      - `Task<MessageDto?> GetMessageAsync(Guid messageId, CancellationToken cancellationToken)`
  - Implementation in infrastructure layer using EF Core
  - Must load messages by `ConversationId` for both direct + group
  - Update `ChatReloadCoordinator` to use the query interface
- Group membership query:
  - `IConversationMemberQueries`:
    - `Task<List<GroupMemberDto>> GetGroupMembersAsync(Guid conversationId, CancellationToken cancellationToken)`
    - `Task<List<GroupMemberWithRouteDto>> GetGroupMembersWithRoutesAsync(Guid conversationId, CancellationToken cancellationToken)`
    - `Task<bool> IsUserGroupAdminAsync(Guid conversationId, Guid selfIdentityId, CancellationToken cancellationToken)`
  - `GroupMemberDto`:
    - `PeerId` (GUID)
    - `DisplayName` (string)
    - `Role` (enum: Member, Admin)
    - `JoinedAtUtc` (timestamp)
  - `GroupMemberWithRouteDto` (extends GroupMemberDto):
    - `DeliveryRoute` (from existing route table: direct or relay peer ID)
    - Use first route in route table (user preference deferred)
- Group details snapshot query:
  - `IGroupDetailsQueries`:
    - `Task<GroupDetailsDto?> GetGroupDetailsAsync(Guid conversationId, CancellationToken cancellationToken)`
  - `GroupDetailsDto`:
    - `ConversationId` (GUID)
    - `Name` (string, nullable)
    - `CreatedAtUtc` (timestamp)
    - `Members` (list of `GroupMemberDto`)
    - `IsUserAdmin` (bool)

Do not reinvent (surgical change):

- Replace only the read-side message loading in `ChatReloadCoordinator`; keep existing debounce/trigger/state sync mechanisms
- Rule (enforced): WPF reads must not call domain repositories

UX rules (existing behavior; do not redesign):

- **Initiating a group**: If the local user initiated group creation, the group conversation shows up in the main window list immediately.
- **Receiving an invite**: If a different (remote or simulated) peer requests the main window to join a group, that request appears in the `PendingInvitations` list of the connection management dialog. The list item should display context like *"Alice invited you to a group: [Name]"*.
- When a conversation has not been bootstrapped yet, use the existing "in progress" view/viewmodel used for 1:1 sessions before X3DH session initiation

Unified identity + schema rule:

- `ConversationId` is the only identifier for direct and group conversations
- Group is represented by `ConversationDbo.Kind == Group` (no separate group GUID needed)
- All wire contracts use `conversation_id` for group routing and operations

## Chunk F — Desktop main window UX (selection + chat)

Goal:

- Group conversations appear/select like direct chats, and the main pane can load + send group messages.

Deliverables:

- Selection plumbing:
  - Selection is stored in `Desktop.Wpf.Features.Sessions.State.SelectedChannelModel.SelectedKey`
  - Extend selection to include a group key type and propagate through:
    - `PeerConnectionStateService` (key construction for groups)
    - `SessionsSidebarViewModel` (key parsing for group type)
    - `SelectedChannelPaneViewModel` (content resolution for groups)
- Message pane behavior:
  - On group selection:
    - Resolve `ConversationId` from selection key
    - Load messages via `IConversationMessageQueries` (Chunk E)
    - Load group details via `IGroupDetailsQueries` (Chunk E)
    - Create `GroupChatViewModel` (composition, not inheritance) for group conversations:
      - Composes existing message loading logic from `ChatStateService`
      - Adds group-specific send behavior wired to `SendGroupMessageCommand`
      - Adds member list display (info bubble on hover shows member names)
      - Reuses existing reactive properties (MessageInput, CanSend, etc.)
  - Send button:
    - In `GroupChatViewModel`: Wired to `SendGroupMessageCommand` (Chunk D)
    - In existing `ChatViewModel`: Routes to `PostTextMessageCommand` for direct conversations
    - Detect conversation kind via query interface
- Pending invitations UI:
  - Extend connection management dialog to show pending group invitations
  - Use `IPendingGroupInvitationQueries` to load pending invites
  - Add Accept/Decline buttons that dispatch `AcceptGroupInviteCommand` / `DeclineGroupInviteCommand`
- Create Group UI trigger:
  - Add a new tab to the connection management dialog for creating groups
  - Use a native WPF `ListBox` with `SelectionMode="Multiple"` to select peers
  - Reuse/extend the existing material-inspired styling in `Desktop.Wpf/Shared/Theme/Styles.xaml` for the ListBox (e.g., custom `ItemTemplate` using existing brushes and hover states)
  - Dispatches `CreateGroupCommand` on confirm
- Bootstrap state indication:
  - Show "in progress" indicator when group exists but no `GroupMasterKey` is present
  - Reuse existing "in progress" view/viewmodel from 1:1 X3DH session initiation

UI decisions (confirmed):

- Use separate `GroupChatViewModel` (composition, not inheritance) for group conversations
- Sidebar: Group items use `IconGroup` from Icons.xaml (pattern: use `StaticResource IconGroup` with `Style="{StaticResource IconText}"`)
- Sidebar: No group metadata displayed (only group name)
- Detail pane (Header Info Bubble): Add a native WPF `ToolTip` attached directly to the group name `TextBlock` (or an adjacent `IconText` block). The ToolTip simply displays the roster of group member names.
- Send button: Wired to group-specific send behavior in `GroupChatViewModel`
- Bootstrap state: Reuse existing "in progress" view/viewmodel, show "Waiting for group key..." with spinner

## Chunk G — Group admin + membership changes

Goal:

- Admin/membership operations exist as write-side commands/handlers and the UI can drive them and refresh via read models.

### G.1: Protobuf & Inbound Processing
- Protobuf extension for membership operations:
  - Extend `GroupContent` in `internal_messaging.proto` to add `oneof` for membership operations:
    - `add_member` (bytes) - target member identity (peer identifier)
    - `remove_member` (bytes) - target member identity (peer identifier)
    - `change_title` (string) - new group name
  - Regenerate protobuf C# types
- Inbound membership operation handling:
  - In `ProcessInternalEnvelopeHandler.GroupMessage` case:
    - For `AddMember`:
      - Validate sender is admin
      - Add to local `GroupMemberDbo`
      - If self is the added member, conversation shows "in progress" until `GroupKeyBootstrap` received
    - For `RemoveMember`:
      - Validate sender is admin
      - Update local `GroupMemberDbo` (set `RemovedAtUtc`)
      - If self is removed, mark conversation as left
    - For `ChangeTitle`:
      - Validate sender is admin
      - Update `GroupStateDbo.Name`

### G.2: Add Member Operation
- Add member command/handler:
  - `AddGroupMemberCommand`:
    - Input: `ConversationId`, `PeerId` to add, `SelfIdentityId`
  - Handler: `AddGroupMemberCommandHandler`:
    - Verify user is admin (via query interface)
    - Verify secure session exists with target peer (via `IDirectSessionRepository`)
    - Load `GroupMasterKey` via `IGroupCryptoStateRepository`
    - Send `ChatEnvelope.create_group` to the **new member**:
      - Populate with current group state (ConversationId, Creator, full current roster, GroupName)
      - Send via `IRemoteEnvelopeSender.SendChatEnvelopeToPeerAsync`
    - Send `GroupKeyBootstrap` to the **new member** via `IRemoteEnvelopeSender`
    - Create `GroupContent.add_member` with target peer identity
    - Encrypt and send as `ChatEnvelope.group_message` to all **existing members**
    - Update local `GroupMemberDbo` (add new member)

### G.3: Remove Member (Cryptographic Eviction)
- Remove member command/handler:
  - `RemoveGroupMemberCommand`:
    - Input: `ConversationId`, `PeerId` to remove, `SelfIdentityId`
  - Handler: `RemoveGroupMemberCommandHandler`:
    - If removing self (PeerId == SelfIdentityId):
      - No admin check required (user can always leave)
      - No key rotation required (user voluntarily leaving, not cryptographic eviction)
      - Create `GroupContent.remove_member` with self peer identity
      - Encrypt and send as `ChatEnvelope.group_message` to all members
      - Update local `GroupMemberDbo` (set `RemovedAtUtc` for self)
      - Mark conversation as left locally
    - If removing other member:
      - Verify user is admin
      - Verify at least 1 admin will remain after removal (throw if removing last admin)
      - Create `GroupContent.remove_member` with target peer identity
      - Encrypt and send as `ChatEnvelope.group_message` to **all current members (including the evictee)** using the *current* epoch key (this allows the evictee's UI to know they were removed)
      - Generate new `GroupMasterKey` (epoch + 1)
      - Update `GroupStateDbo.Epoch`
      - Persist new master key via `IGroupCryptoStateRepository`
      - Update local `GroupMemberDbo` (set `RemovedAtUtc` for removed member)
      - Send `GroupKeyBootstrap` to all **remaining members** via `IRemoteEnvelopeSender`
      - Throw if user attempts to send group message during epoch transition (UI handles blocking)
  - UI uses `RemoveGroupMemberCommand` for both "Remove Member" (admin removing others) and "Leave Group" (self-removal)
- Epoch cutover constraint:
  - During removal/key rotation, domain service throws if user attempts to send group message
  - UI is responsible for blocking send button during epoch transition: Disable the Send button (do not change placeholder or show banner)
  - Bootstrap fanout is considered complete once all bootstrap messages are sent (fire-and-forget, no delivery confirmation required)

### G.4: Change Title Operation
- Change title command/handler (optional):
  - `ChangeGroupTitleCommand`:
    - Input: `ConversationId`, `NewTitle`, `SelfIdentityId`
  - Handler: `ChangeGroupTitleCommandHandler`:
    - Verify user is admin
    - Create `GroupContent.change_title` with new title
    - Encrypt and send as `ChatEnvelope.group_message` to all members
    - Update `GroupStateDbo.Name`

### G.5: UI Behavior & Dialogs
- Group action placement:
  - Add a menu in the space above the chat itself (`ChatView.xaml`) for group-specific actions (Add Member, Remove Member, Leave Group, Change Title)
- Dialog Implementations:
  - Use `IWindowManager.ShowFor<ViewModelType>()` to open dialogs for Add Member, Remove Member, and Change Title (do not instantiate `Window` objects directly).
  - Register new ViewModel/View mappings in `Desktop.Wpf/Shared/Windowing/ViewMappings.xaml`.
- UI behavior (reactive pattern):
  - Expose commands as `AsyncRelayCommand`s
  - Show progress/errors via reactive properties
  - Reload from source after success
  - Refresh sidebar + group details + message pane via query/read models (Chunk F)

Do not reinvent (reuse existing patterns):

- Use MediatR command/handler pattern for operations
- Use existing envelope sender for fanout
- Use query/read-model interfaces for authorization checks and UI refresh

## Chunk H — Simulator parity gate

Goal:

- Simulator can drive and validate the same group flows as the main app.

Deliverables:

- Extend simulator plumbing to cover:
  - Create group (via `CreateGroupCommand` / envelope)
  - Receive/process `ChatEnvelope.create_group`
  - Receive/process `ChatEnvelope.group_key_bootstrap`
  - Send/receive `ChatEnvelope.group_message`
- Update simulator UI and outbound interception:
  - `Desktop.Wpf/Features/Simulator/SimulatorChatViewModel.cs` - handle group message sending
  - `Desktop.Wpf/Features/Simulator/SimulatorOutboundInterceptor.cs` - intercept group messages:
    - Add switch cases in `InterceptDeliverOpaqueMessageAsync` to handle `ChatEnvelope.CreateGroup`, `ChatEnvelope.GroupKeyBootstrap`, `ChatEnvelope.GroupMessage`
    - Pattern follows existing message type interception in the interceptor
  - Add envelope interception for new message types in simulator wiretap
- Add simulator test scenarios:
  - Create group between simulated peers
  - Accept/decline group invitations
  - Send group messages
  - Add/remove members
  - Verify cryptographic eviction on member removal

## Chunk I — Tests + coverage gates

Goal:

- Group messaging end-to-end behavior is covered by tests.

Deliverables:

- Integration path test:
  - Create group -> bootstrap delivered -> send `GroupMessage` -> decrypt on recipient -> plaintext persisted
- Negative test:
  - `GroupMessage` before bootstrap does not persist plaintext
- Membership operation tests:
  - Add member operation propagates to all members
  - Remove member operation triggers key rotation
  - Removed member cannot decrypt new messages
- Pending invitation tests:
  - Pending invitation persisted on receipt
  - Accept creates conversation and membership
  - Decline does not create conversation
- Authorization tests:
  - Non-admin cannot add/remove members
  - Non-admin cannot change group title

## Chunk J — Final verification

Goal:

- All group functionality works end-to-end in both main app and simulator.

Deliverables:

- End-to-end smoke test:
  - Create group via main app UI
  - Accept invitation on second peer
  - Send messages from both peers
  - Add third member
  - Remove member
  - Verify all operations complete successfully
- Simulator parity verification:
  - Run same smoke test in simulator
  - Verify identical behavior
- Performance check:
  - Bootstrap distribution completes in reasonable time
- Documentation:
  - Update any remaining documentation that references old group system
  - Ensure code comments are accurate

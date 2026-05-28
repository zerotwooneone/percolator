
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
5. **ByteArray Factory Methods:** ByteArray types (GroupMasterKey, GroupId, BlobKey, Ciphertext) have no public constructor. Use factory methods:
   - `FromBytesOwned(byte[])` - unsafe no-copy, use only when the array is strictly owned by the called code and never accessed elsewhere
   - `FromBytes(byte[])` - safe copy, use when the array is not owned by the called code or could be mutated elsewhere (e.g., EF Core entities, protobuf messages)
   - `FromSpan(ReadOnlySpan<byte>)` - useful for copying one ByteArray type to another

---

## Group Messaging Implementation Plan (V2)

**Goal:** Provide a single, comprehensive list of the architectural, cryptographic, networking, and UX design goals that drive the entire group messaging feature. It explicitly tracks what has already been built and what requires modification or new implementation.

**Status Key:**
- `[IMPLEMENTED]`: Core foundation already exists in the codebase.
- `[NEEDS MODIFICATION]`: Code exists but must be updated to support the Layered ZK over 1:1 Transport architecture or admin features.
- `[PENDING]`: Not yet built.

### 1. Architecture & Cryptography (Layered ZK over 1:1 Transport)
- **Signal Group V2 Inspiration:** Adapt Signal's central-server Group V2 protocol into a decentralized P2P paradigm. `[TARGET DESIGN]`
- **Group Anchor:** A single 32-byte `GroupMasterKey` serves as the root secret. It deterministically derives the `GroupId` (for routing/context) and `BlobKey` (for symmetric payload encryption). `[IMPLEMENTED]` *(Note: GroupId derivation exists but is currently unused; it will become the routing identifier).*
- **Transport vs. Application Separation:** `[NEEDS MODIFICATION]`
  - **Application Layer (ZK Envelope):** Senders encrypt the payload once using the `BlobKey` and attach a Zero-Knowledge (ZK) proof asserting: "I am an authorized member of `group_id X`". This becomes the `GroupMessageEnvelope`.
  - **Transport Layer (1:1 Session):** Senders wrap the `GroupMessageEnvelope` in a standard 1:1 Double Ratchet session addressed directly to their relay (or direct peers). This protects the relay from unauthenticated DoS attacks on its expensive ZK circuits.
- **Relay Blinded Fanout:** `[PENDING]`
  - Relays decrypt the 1:1 transport envelope, *transiently* know the sender's identity, but immediately strip it.
  - The relay validates the inner ZK proof mathematically.
  - If valid, the relay uses its own synchronized, blinded routing table for `group_id X` to fan out the message to connected routing tokens, completely blind to the social graph.
  - Direct peers act as a "relay of one", validating the ZK proof before accepting the payload locally.
- **Cryptographic Eviction (Epochs):** `[PENDING]`
  - Evicting a member requires generating a *new* `GroupMasterKey` (epoch + 1).
  - The new key is distributed strictly via 1:1 Double Ratchet sessions to remaining members (bootstrap payloads).
  - The admin must also push an updated blinded routing table to the relays via an administrative ZK proof.
  - The evicted member, lacking the new key, cannot derive the new `BlobKey` to read messages, nor can they generate valid ZK proofs.

### 2. Protobuf & Network Contracts
- **`internal_messaging.proto` Extensions:** 
  - `GroupContent` message with `text_message` (string). `[IMPLEMENTED]`
  - `GroupContent` `oneof` extensions for `add_member`, `remove_member`, and `change_title`. `[PENDING]`
- **`ChatEnvelope` Cases:** 
  - `create_group`: Contains initial conversation ID, creator, roster, and group name. `[IMPLEMENTED]`
  - `group_key_bootstrap`: Distributes the encrypted `GroupMasterKey` via 1:1 sessions. `[IMPLEMENTED]`
  - `group_message`: The envelope containing group payloads. `[IMPLEMENTED]` -> `[NEEDS MODIFICATION]` (Must be updated to include `group_id` bytes and `zero_knowledge_member_proof`).

### 3. Domain-Driven Design & Persistence
- **Aggregate Segregation:** Strict DDD maintaining `Conversation` as the aggregate root. `[IMPLEMENTED]` -> `[NEEDS MODIFICATION]` (Expand to enforce epoch rotation constraints).
- **Repositories:** Explicit separation between `IDirectConversationRepository`, `IGroupConversationRepository`, and `IMessageRepository`. `[IMPLEMENTED]`
- **Database Contexts:** 
  - `PercolatorDbContext`: Stores `ConversationDbo` (Kind=Group), `GroupMemberDbo`, `GroupStateDbo`, and `PendingGroupInvitationDbo`. `[IMPLEMENTED]`
  - `CryptoDbContext`: Stores the highly sensitive `GroupCryptoStateDbo`. `[IMPLEMENTED]`
- **Cryptographic Interfaces:** Implement `IGroupCryptographyService`, `IGroupMessageCryptographyService`, and `IGroupCryptoStateRepository`. `[IMPLEMENTED]` -> `[NEEDS MODIFICATION]` (Must be expanded to support ZK proof generation and verification).

### 4. Commands & Handlers (CQRS)
- **Outbound Handlers:** 
  - `CreateGroupCommand` / `AcceptGroupInviteCommand` / `DeclineGroupInviteCommand`. `[IMPLEMENTED]`
  - `SendGroupMessageCommand`: `[IMPLEMENTED]` -> `[NEEDS MODIFICATION]` (Currently sends basic group message; must be updated to generate ZK proof and properly execute the Layered ZK Transport wrap).
  - `AddGroupMemberCommand` / `RemoveGroupMemberCommand` / `ChangeGroupTitleCommand`. `[PENDING]`
- **Inbound Handlers:** 
  - `ProcessInternalEnvelopeHandler`: Processes `group_message` cases. `[IMPLEMENTED]` -> `[NEEDS MODIFICATION]` (Currently just decrypts; must be updated to validate ZK proof and handle Relay Blinded Fanout logic if the node is acting as a relay).
- **Read Models (Queries):** 
  - `IConversationMemberQueries` with route resolution. `[IMPLEMENTED]`
  - `IPendingGroupInvitationQueries`. `[IMPLEMENTED]`
  - `IConversationMessageQueries` and `IGroupDetailsQueries`. `[PENDING]`

### 5. UI/UX Decisions & Plumbing
- **ViewModels:** Composition (`GroupChatViewModel` wrapping `ChatStateService`). `[PENDING]`
- **Sidebar & Selection:** Plumb selection through `SelectedChannelModel.SelectedKey`, `PeerConnectionStateService`, and `SessionsSidebarViewModel`. Group items use `IconGroup` (display name only). `[PENDING]`
- **Group Details & Roster:** Native WPF `ToolTip` attached directly to the group name `TextBlock` in the chat header. `[PENDING]`
- **Dialogs & Windowing:** Use `IWindowManager.ShowFor<ViewModelType>()`. "Create Group" tab in connection management with `ListBox` (Multiple selection). Pending invites with Accept/Decline. `[PENDING]`
- **Chat Interface:** Dropdown action menu for group actions. "In progress" bootstrap state spinner. `[PENDING]`
- **Drafts:** Keep message drafts strictly in-memory (`SessionContext.Draft`). `[IMPLEMENTED]` (Decision settled).

### 6. Simulator Parity & Testing Gates
- **Simulator Parity:** Absolute 1:1 parity with the main app. Update `SimulatorChatViewModel` and `SimulatorOutboundInterceptor.InterceptDeliverOpaqueMessageAsync` to handle group cases. `[PENDING]`
- **Testing Targets:**
  - **Integration Paths:** E2E smoke tests (Create -> Invite -> Accept -> Bootstrap -> Send -> Decrypt -> Persist). `[PENDING]`
  - **Negative/Auth Tests:** Auth boundaries, ZK mathematical rejection, cryptographic eviction enforcement. `[PENDING]`

---

## AI Execution Chunks

The following chunks are designed to be executed sequentially by AI agents. Each chunk represents a cohesive vertical slice of the implementation that minimizes context switching while maximizing delivered value.

### Chunk 1: ZK Cryptographic Core & Protocol Contracts
**Focus:** Define the mathematical boundaries, network payloads, and strict domain rules for the ZK architecture.

**1. Protocol Contracts (`Percolator.Contracts/Protos/internal_messaging.proto`):**
- **Action:** Update `GroupContent` to use a `oneof` payload block.
  ```protobuf
  message GroupContent {
    oneof content {
      string text_message = 1;
      bytes add_member = 2;       // SPKI identity of the added peer
      bytes remove_member = 3;    // SPKI identity of the removed peer
      string change_title = 4;    // New group name
    }
  }
  ```
- **Action:** Update `GroupMessage` to add `optional bytes zero_knowledge_member_proof = 8;`.
- **Action:** Ensure you rebuild the `Percolator.Contracts` project to regenerate the C# types.

**2. Crypto Interfaces (`Percolator.Cryptography`):**
**CRITICAL:** For this milestone, we will use a "Plumbing Stub" for the ZK math. Real ZK proofs require complex public parameter distribution which is out of scope. We will build the entire data pipeline (Keys, DBs, Routing) but the actual crypto logic will be stubbed.
- **Action:** In `IGroupCryptographyService.cs`, add three new methods:
  - `byte[] GenerateGroupVerificationKey(GroupMasterKey masterKey);` (Stub: return `new byte[] { 0x02 };`)
  - `byte[] GenerateZeroKnowledgeMembershipProof(GroupMasterKey masterKey);` (Stub: return `new byte[] { 0x01 };`)
  - `bool VerifyZeroKnowledgeMembershipProof(GroupId groupId, ReadOnlySpan<byte> verificationKey, ReadOnlySpan<byte> proof);` (Stub: return `proof.SequenceEqual(new byte[] { 0x01 });`)
- **Action:** In `Percolator.Infrastructure/Chat/Cryptography/GroupCryptographyService.cs`, implement these methods with the defined stubs.
- **Action:** Create `Percolator.CryptographyTests/GroupCryptoRoundtripTests.cs` modeled after `SecureSessionX3dhRoundtripTests.cs`. Build a test `Sender_encrypts_and_proves_Relay_verifies_Receiver_decrypts` that:
  1. Generates a `GroupMasterKey`.
  2. Derives the `GroupId` and `VerificationKey`.
  3. **Sender:** Encrypts a `GroupContent` payload and generates a ZK proof.
  4. **Relay:** Uses `VerifyZeroKnowledgeMembershipProof` with the `GroupId`, `VerificationKey`, and `proof`. Assert it returns true.
  5. **Receiver:** Decrypts the ciphertext using the `GroupMasterKey` and asserts the payload matches the original.
  6. **Negative Test:** Verify that tampering with the `proof` bytes causes `VerifyZeroKnowledgeMembershipProof` to return false.

**3. Domain Constraints (`Percolator.Chat` & `Percolator.Infrastructure/Chat/Persistence`):**
- **Action:** In `GroupState.cs`, add `public bool IsEpochCutoverPending { get; private set; }`. Update constructor.
- **Action:** In `GroupState.cs`, add methods `public void BeginEpochCutover(DateTimeOffset when)` (sets true) and `public void CompleteEpochCutover(DateTimeOffset when)` (sets false).
- **Action:** In `GroupStateDbo.cs`, add `public bool IsEpochCutoverPending { get; set; }`. 
- **Action:** Run EF Core Migration to add `IsEpochCutoverPending` to `GroupStates` table. Update `SqliteGroupConversationRepository.cs` to map this property between DBO and Domain.
- **Action:** In `GroupConversation.cs`, add `public bool IsEpochCutoverPending => State.IsEpochCutoverPending;`.
- **Action:** In `GroupConversation.cs`, update `AddMember`, `RemoveMember`, and `ChangeName` to throw `InvalidOperationException("Cannot modify group during pending epoch cutover.")` if `IsEpochCutoverPending` is true. Add `public void BeginEpochCutover(DateTimeOffset when)` and `public void CompleteEpochCutover(DateTimeOffset when)` that delegate to `State`.

### Chunk 2: The Layered ZK Transport Pipeline
**Focus:** Implement the outbound wrapping and inbound routing of group messages.

**1. Relay State (`Percolator.Chat` & `Percolator.Infrastructure/Chat/Persistence`):**
- **Action:** Create domain interface `IBlindedRoutingTableRepository` with `Task UpsertRouteAsync(GroupId groupId, ReadOnlySpan<byte> verificationKey, IEnumerable<PeerId> routingTokens, CancellationToken ct);` and `Task<(List<PeerId>? Routes, byte[]? VerificationKey)> GetRoutesAsync(GroupId groupId, CancellationToken ct);`.
- **Action:** Create `BlindedRoutingTableDbo.cs` with `Id` (Guid), `GroupIdBytes` (byte[]), `VerificationKeyBytes` (byte[]), and `RoutingPeerIdsJson` (string).
- **Action:** Implement `SqliteBlindedRoutingTableRepository.cs` and add EF Core Migration for the new DBO.

**2. Outbound (Sender) in `SendGroupMessageCommandHandler.cs`:** 
- **Action:** Modify the handler to use `_groupCryptoService.DeriveGroupId()` and `_groupCryptoService.GenerateZeroKnowledgeMembershipProof()`.
- **Action:** Update the protobuf `GroupMessage` construction to set `group_id` (via `GroupId.Value`) and `zero_knowledge_member_proof`.
- **Action:** Refactor the delivery loop. Group active members by delivery path. If a member's `RouteSelection.Relay` is not null, they are a relay recipient. If null, direct recipient.
- **Action:** Use `IRemoteEnvelopeSender.SendChatEnvelopeToPeerAsync()` to send exactly ONE envelope to each unique Direct Peer and exactly ONE envelope to each unique Relay Peer. *(This automatically fulfills the Transport Layer 1:1 encryption requirement)*.

**3. Inbound Processing in `ProcessInternalEnvelopeHandler.cs` (`GroupMessage` case):**
- **Action:** Inject `IBlindedRoutingTableRepository`, `IGroupCryptographyService`, and `IRemoteEnvelopeSender`.
- **Action (Relay Branch):** Read `GroupId` from `group_message.group_id`. Call `GetRoutesAsync(groupId)`. If it returns a route and a `VerificationKey`:
  - Verify `_groupCryptoService.VerifyZeroKnowledgeMembershipProof(groupId, verificationKey, proof)`. If false, drop the message.
  - Create a new, identical `ChatEnvelope` carrying the exact same `GroupMessage`.
  - Loop over the routing tokens (`PeerId`s). If `token != senderPeerId` (don't echo back), use `_envelopeSender.SendChatEnvelopeToPeerAsync()` to forward it. *Do not try to decrypt it.*
- **Action (Local Branch):** 
  - Call `IGroupCryptoStateRepository.GetConversationIdByGroupIdAsync` (you will need to implement this reverse lookup in Chunk 4, but add the call here). 
  - If a local `ConversationId` is found, fetch the `GroupMasterKey`, derive the `BlobKey`, decrypt the ciphertext using `_cryptoService.DecryptGroupContent()`, and persist it via `_messageWriter.AddTextMessageAsync()`.

**4. Dead Code Removal:**
- **In `SendGroupMessageCommandHandler.cs`:** Delete the `CreateGroupMessageEnvelope` method (lines 151-161). This method will be replaced by a new implementation that sets `group_id` and `zero_knowledge_member_proof`.
- **In `ProcessInternalEnvelopeHandler.cs`:** Delete the entire `ChatEnvelope.MessageOneofCase.GroupMessage` switch case (lines 417-491). This will be replaced by the new ZK validation and Relay fanout logic.

### Chunk 3: Administrative Operations & Epoch Cutover
**Focus:** Handle group state mutation, membership eviction, and routing table synchronization.

**1. Protobuf Admin Updates (`Percolator.Contracts/Protos/internal_messaging.proto`):**
- **Action:** Add `sync_blinded_routing_table` to `ChatEnvelope` message cases.
- **Action:** Define `message SyncBlindedRoutingTable { bytes group_id = 1; bytes zero_knowledge_admin_proof = 2; repeated bytes routing_peer_ids = 3; bytes group_verification_key = 4; }`. Rebuild contracts.

**2. Add Member Command (`Percolator.Application/Apps/Chat/Handlers/AddGroupMemberCommandHandler.cs`):** 
- **Action:** Create `AddGroupMemberCommand(ConversationId ConversationId, PeerId PeerToAdd, int SelfIdentityId)`.
- **Action:** In handler: Load `GroupConversation`. Verify sender is an Admin.
- **Action:** Send existing `ChatEnvelope.create_group` to the *new member* (populating full roster/name) via 1:1 `IRemoteEnvelopeSender`.
- **Action:** Send `ChatEnvelope.group_key_bootstrap` to the *new member*.
- **Action:** Create `GroupContent.add_member` (setting the new member's SPKI) and encrypt it. Dispatch to *existing members* via the Layered ZK Transport logic from Chunk 2.
- **Action:** Collect unique Relay Peers from the member route table (where `member.DeliveryRoute.PeerId != member.PeerId`). Call `_groupCryptoService.GenerateGroupVerificationKey(masterKey)` and send `SyncBlindedRoutingTable` to relays via `IRemoteEnvelopeSender`.
- **Action:** Call `conversation.AddMember()` and `_repository.UpdateAsync()`.

**3. Remove Member & Epoch Cutover (`Percolator.Application/Apps/Chat/Handlers/RemoveGroupMemberCommandHandler.cs`):** 
- **Action:** Create `RemoveGroupMemberCommand(ConversationId ConversationId, PeerId PeerToRemove, int SelfIdentityId)`.
- **Action:** In handler: Verify Admin (unless self-leaving). Call `conversation.BeginEpochCutover()`.
- **Action:** Encrypt `GroupContent.remove_member` using the *current* epoch key. Dispatch to ALL current members (including the evictee) via Layered ZK Transport.
- **Action:** Call `_groupCryptoService.GenerateGroupMasterKey()`. Call `conversation.IncrementEpoch()`.
- **Action:** Distribute the *new* key via `group_key_bootstrap` over 1:1 sessions to the *remaining* members.
- **Action:** Send updated `SyncBlindedRoutingTable` to relays for the new `group_id`.
- **Action:** Call `conversation.CompleteEpochCutover()`. Upsert new key to `IGroupCryptoStateRepository` and update `_repository`.

**5. Inbound Processing (`ProcessInternalEnvelopeHandler.cs`):** 
- **Action:** Add `ChatEnvelope.MessageOneofCase.SyncBlindedRoutingTable` block. Validate `zero_knowledge_admin_proof` (stub returning true). Upsert the routing peer IDs and `group_verification_key` into `IBlindedRoutingTableRepository`.
- **Action:** In the existing `GroupMessage` local branch, switch on `GroupContent.contentCase`:
  - `AddMember`: Call `conversation.AddMember()`.
  - `RemoveMember`: Call `conversation.RemoveMember()`.
  - `ChangeTitle`: Call `conversation.ChangeName()`.
  - Save to `IGroupConversationRepository`.

### Chunk 4: UI Data Layer & Plumbing Coordinators
**Focus:** Bridge the backend handlers to the frontend WPF application via fast read models and fix inbound routing lookups.

**1. GroupId Reverse Lookup (`Percolator.Infrastructure/Chat/Persistence`):**
- **Action:** In `GroupCryptoStateDbo.cs`, add `public byte[] GroupIdBytes { get; set; } = Array.Empty<byte>();`.
- **Action:** Generate an EF Core Migration to add this column to `GroupCryptoStates`.
- **Action:** Update `SqliteGroupCryptoStateRepository.UpsertGroupMasterKeyAsync` to compute `_groupCryptographyService.DeriveGroupId()` and store it in `GroupIdBytes`.
- **Action:** Add `Task<ConversationId?> GetConversationIdByGroupIdAsync(GroupId groupId, CancellationToken ct)` to `IGroupCryptoStateRepository` and implement it using a simple `SingleOrDefaultAsync` query against `GroupIdBytes`.

**2. EF Core Queries (`Percolator.Application/Apps/Chat/Queries`):** 
- **Action:** Build `IGroupDetailsQueries.cs` interface with `Task<GroupDetailsDto?> GetGroupDetailsAsync(ConversationId conversationId, int selfIdentityId, CancellationToken ct)`. `GroupDetailsDto` should contain `Name`, `CreatedAtUtc`, `IsUserAdmin`, and `List<GroupMemberDto>`.
- **Action:** Implement `SqliteGroupDetailsQueries.cs` in `Percolator.Infrastructure/Chat/Queries` by joining `ConversationDbo`, `GroupStateDbo`, and `GroupMemberDbo`. Register it in DI.

**3. Reactive Coordinators (`Desktop.Wpf/Features/Chat/ChatReloadCoordinator.cs`):** 
- **Action:** Decouple the UI reload trigger from `DirectSessionId` (groups don't use direct sessions). 
  - Change `IChatReloadCoordinator.TriggerReloadForConversation` to take just `(ConversationId conversationId, int selfIdentityId)`. 
  - Update `ReloadTrigger` record to only hold `ConversationId` and `SelfIdentityId`.
  - In `ReloadCoreAsync`, fetch messages and push them to `_state.SyncMessages(conversationId, snapshots)` (you will need to update `ChatStateService`'s internal `ConcurrentDictionary` and all public methods `SyncMessages`, `OptimisticInsert`, `MarkAsDelivered` to index by `ConversationId` instead of `DirectSessionId`).
- **Action:** Update call sites for `TriggerReloadForConversation` (specifically in `ChatStateUpdateHandlers.cs` for `TextMessagePostedEvent` and `TextMessageReceivedEvent`) to match the new signature (remove `DirectSessionId`).
- **Action:** Ensure `SendGroupMessageCommandHandler` and `ProcessInternalEnvelopeHandler.GroupMessage` publish a MediatR `TextMessagePostedEvent` upon persisting a message. Update `ChatReloadCoordinator` to subscribe to this event.

**4. Dead Code Removal:**
- **In `ChatReloadCoordinator.cs`:**
  - Delete the `DirectSessionId sessionId` parameter from `IChatReloadCoordinator.TriggerReloadForConversation` (line 22).
  - Delete the `DirectSessionId SessionId` field from the `ReloadTrigger` record (line 28).
  - Delete the `sessionId` argument in the `TriggerReloadForConversation` implementation (line 67).
  - Delete the `DirectSessionId sessionId` parameter from `ReloadCoreAsync` (line 83).
  - Delete the `sessionId` argument in the `_state.SyncMessages(sessionId, snapshots)` call (line 100).
- **In `ChatStateService.cs`:**
  - Replace the `ConcurrentDictionary<DirectSessionId, ObservableList<ChatMessageModel>>` declaration with `ConcurrentDictionary<ConversationId, ObservableList<ChatMessageModel>>` (line 12).
  - Update all method signatures to use `ConversationId` instead of `DirectSessionId`: `GetOrAddSessionMessagesList` (line 15), `SyncMessages` (line 18), `OptimisticInsert` (line 38), `MarkAsDelivered` (line 50).
- **In `ChatStateUpdateHandlers.cs`:**
  - Delete the code that extracts and passes `DirectSessionId` to `ChatStateService` and `ChatReloadCoordinator` in `TextMessagePostedEvent` (lines 25, 27, 43) and `TextMessageReceivedEvent` (lines 49, 51-55).

### Chunk 5: Core Chat UI & ViewModels
**Focus:** Render the group chat experience in the main window.

**1. Selection Plumbing (`Desktop.Wpf/Features/Sessions`):** 
- **Action:** In `Models/PeerConnectionKey.cs`, add `GroupConversation` to `SecureChannelKeyType`. Add `public static PeerConnectionKey FromGroupConversationId(Guid conversationId)`.
- **Action:** In `SqlitePeerConnectionSidebarQueries.cs`, update the SQL query to UNION the existing direct peers with group conversations (`SELECT ... FROM Conversations WHERE Kind = 1`). Map them to `SidebarPeerConnectionDto` with a new `IsGroup` flag.
- **Action:** In `SessionsSidebarViewModel.cs`, handle group items by forcing the icon to `StaticResource IconGroup` and hiding any relay/direct sub-text.

**2. Composition ViewModel (`Desktop.Wpf/Features/Chat/GroupChatViewModel.cs`):** 
- **Action:** Create `GroupChatViewModel` extending `ObservableObject`. Inject `ChatStateService`, `IMediator`, and `IGroupDetailsQueries`.
- **Action:** Expose `IReadOnlyObservableList<ChatMessageSnapshot> Messages` by delegating to `_state.GetMessages(ConversationId)`.
- **Action:** Expose `ReactiveProperty<string> RosterNames` (e.g., "Alice, Bob, Charlie") populated via `IGroupDetailsQueries`.
- **Action:** Expose `ReactiveProperty<bool> IsEpochCutoverPending` and bind it so the Send button `CanExecute` is false when true.
- **Action:** Implement `PostTextMessageCommand` that dispatches `SendGroupMessageCommand` via MediatR.
- **Action:** Update `SelectedChannelPaneViewModel.cs` to resolve `GroupChatViewModel` when the `SelectedKey.Type == GroupConversation`.

**3. Chat View Updates (`Desktop.Wpf/Features/Chat/ChatView.xaml`):** 
- **Action:** In the Chat Header (where the peer name is displayed), add a `<TextBlock.ToolTip>` bound to `RosterNames` so users can hover to see the member list.
- **Action:** In the top-right of the Chat Header, add a Dropdown Menu (`<Menu>` with a `<MenuItem Header="...">` or a styled material popup) containing:
  - Add Member (triggers `ShowFor<AddGroupMemberDialogViewModel>`)
  - Remove Member (triggers `ShowFor<RemoveGroupMemberDialogViewModel>`)
  - Change Title (triggers `ShowFor<ChangeGroupTitleDialogViewModel>`)
- **Action:** Bind the "Waiting for group key..." spinner (`BootstrapState` visibility) to show if the current user hasn't received the `GroupMasterKey` yet.

### Chunk 6: Dialogs, Simulator Parity & Testing
**Focus:** Finish the UX flows for group lifecycle and verify end-to-end correctness.

**1. Group Creation & Invites UX (`Desktop.Wpf/Features/Sessions/ConnectionManagementDialogWindow.xaml`):** 
- **Action:** Add a "Create Group" TabItem. Implement a `<ListBox>` bound to the user's connected peers (use `PeerConnectionStateService.Connections`). Set `SelectionMode="Multiple"`. Use existing custom material-inspired styling (e.g., `Style="{StaticResource ListBoxItemStyle}"`).
- **Action:** In `ConnectionManagementDialogViewModel.cs`, add `AsyncRelayCommand CreateGroupCommand` which reads `SelectedPeers`, requests a group name via a simple prompt, and dispatches the backend `CreateGroupCommand`.
- **Action:** Expand the Pending Requests tab in the dialog to bind to `IPendingGroupInvitationQueries.GetPendingInvitationsAsync`. Add `AcceptGroupInviteCommand` and `DeclineGroupInviteCommand`.

**2. Group Action Dialogs (`Desktop.Wpf/Features/Chat/Dialogs`):**
- **Action:** Create `AddGroupMemberDialogViewModel.cs` & `.xaml`. Provide a list of non-member peers to select. On confirm, dispatch backend `AddGroupMemberCommand`.
- **Action:** Create `RemoveGroupMemberDialogViewModel.cs` & `.xaml`. Provide a list of current members (loaded from `IGroupDetailsQueries`). On confirm, dispatch backend `RemoveGroupMemberCommand`.
- **Action:** Create `ChangeGroupTitleDialogViewModel.cs` & `.xaml`. Simple text input. Dispatches backend `ChangeGroupTitleCommand`.
- **Action:** Register all three pairs in `Desktop.Wpf/Shared/Windowing/ViewMappings.xaml`.

**3. Simulator Parity (`Desktop.Wpf/Features/Simulator`):** 
- **Action:** Update `SimulatorOutboundInterceptor.InterceptDeliverOpaqueMessageAsync()`. Add explicit switch cases for `ChatEnvelope.MessageOneofCase.CreateGroup`, `GroupKeyBootstrap`, `GroupMessage`, and `SyncBlindedRoutingTable`. Ensure the simulated wiretap logs them and forwards them to the target simulated peer.
- **Action:** Update `SimulatorChatViewModel.cs` to correctly handle sending messages when the active simulated conversation is a group (dispatching the backend `SendGroupMessageCommand` instead of the 1:1 command).

**4. Testing Gates:** 
- **Action:** Write an Integration Test demonstrating the happy path: `CreateGroupCommand` -> Simulated recipient accepts -> `GroupKeyBootstrap` is delivered -> Sender dispatches `GroupMessage` (ZK Layered) -> Recipient resolves `GroupId`, validates ZK proof, decrypts, and persists to DB.
- **Action:** Write negative tests: (1) Ensure `RemoveGroupMemberCommand` accurately drops the evictee from the `GroupKeyBootstrap` 1:1 distribution list for the new epoch. (2) Ensure the `ProcessInternalEnvelopeHandler` Relay branch silently drops `GroupMessage` envelopes with invalid `zero_knowledge_member_proof`s.

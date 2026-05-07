## Chunk A

### Research Plan: Strip "Online" Concept from Project

**Objective:** Remove the concept of a peer being "online" from the project, similar to Signal protocol/app which does not have this concept. Signal operates on an asynchronous, message-queue-based model where peers are assumed to be reachable via their relay or direct routes without explicit online/offline status.

**Rationale:** The "online" concept introduces unnecessary complexity and does not align with the Signal protocol's design philosophy. Messages should be queued and delivered when possible, regardless of whether the recipient is "online" at any given moment.

---

### Phase 1: Identify All "Online" References

**1.1 Model Layer**
- **SimulatedPeerModel.cs** (Lines 55, 85, 90-98, 146, 296, 373, 483)
  - Constructor parameter `bool isOnline` (line 55)
  - Property `public ReactiveProperty<bool> IsOnline` (line 146)
  - Initialization: `IsOnline = new ReactiveProperty<bool>(isOnline)` (line 85)
  - Logic: Sets `UiState` to `Offline` if `isOnline` is false (lines 90-98)
  - Logic: `ClearRuntimeState()` uses `IsOnline.Value` to determine UiState (line 296)
  - Logic: `Freeze()` includes `IsOnline: IsOnline.Value` in snapshot (line 373)
  - Disposal: `IsOnline.Dispose()` called (line 483)
  - **Removal Impact:** Need to remove constructor parameter, property, initialization, and all logic that depends on it

- **SessionContext.cs** (Line 12)
  - Property `public ReactiveProperty<bool> IsOnline` (line 12)
  - No logic depends on it in this file
  - **Removal Impact:** Simple property removal

- **SessionListItem.cs** (Line 17)
  - Property `public BindableReactiveProperty<bool> IsOnline` (line 17)
  - No logic depends on it in this file
  - **Removal Impact:** Simple property removal

**1.2 ViewModel Layer**
- **ChatViewModel.cs** (Lines 22, 57, 124)
  - Property `public BindableReactiveProperty<bool> IsOnline` (line 22)
  - Initialization: `IsOnline = _sessionContext.IsOnline.ToBindableReactiveProperty(false)` (line 57)
  - Disposal: `IsOnline` disposed (line 124)
  - **Removal Impact:** Remove property, initialization, and disposal

- **SimulatedPeerCardViewModel.cs** (Lines 51-53, 55, 86-92, 164, 409)
  - Property `public BindableReactiveProperty<bool> IsOnline` (line 164)
  - Initialization: `IsOnline = _model.IsOnline.ToBindableReactiveProperty()` (line 51-53)
  - Two-way binding: `_bag.Add(IsOnline.Subscribe(newValue=> _model.IsOnline.Value = newValue))` (line 55)
  - Commands: `ToggleOnlineCommand` and `TogglePowerCommand` both toggle `IsOnline` (lines 86-92)
  - Disposal: `IsOnline.Dispose()` called (line 409)
  - **Removal Impact:** Remove property, initialization, two-way binding, commands, and disposal. Remove the toggle buttons from XAML.

- **SimulatedPeerItemViewModel.cs** (Lines 101-105, 211)
  - Command `ToggleOnlineCommand` toggles `_model.IsOnline` (lines 101-105)
  - No `IsOnline` property in this ViewModel
  - **Removal Impact:** Remove command and toggle button from XAML.

- **PeerConnectionListItemViewModel.cs** (Lines 18, 48-53)
  - Property `public BindableReactiveProperty<bool> IsOnline` (line 18)
  - Logic: Derived from `model.Status` - true if status is Direct, Relay, or Group (lines 48-53)
  - **Removal Impact:** This is derived from connection status, not online/offline. Decision: RENAME property to `IsConnectionEstablished` to accurately reflect its meaning.

**1.3 UI Layer**
- **InitialsAvatar.xaml.cs** (Lines 23-30)
  - Dependency property `IsOnlineProperty` (line 23-24)
  - Property getter/setter `IsOnline` (lines 26-30)
  - **Removal Impact:** Remove dependency property and getter/setter

- **InitialsAvatar.xaml** (Lines 15-17)
  - Presence dot visualization `PresenceDot` (line 15)
  - Bound to `IsOnline` with `BoolToVis` converter (line 17)
  - **Removal Impact:** Remove binding to `IsOnline`. The ellipse will be repurposed as a notification indicator for unread changes (e.g., unread messages). This will be implemented in a separate chunk.

- **ChatView.xaml** (Line 30)
  - Binding: `IsOnline="{Binding IsOnline.Value}"`
  - **Removal Impact:** Remove binding

- **SessionsSidebarView.xaml** (Line 54)
  - Binding: `IsOnline="{Binding IsOnline.Value}"`
  - **Removal Impact:** Remove binding

**1.4 Persistence Layer**
- **JsonSimulatorStateRepository.cs** (Lines 194, 245, 591-599)
  - Deserialization: `isOnline: dto.IsOnline` passed to SimulatedPeerModel constructor (line 194)
  - Serialization: `IsOnline = model.IsOnline` assigned to DTO (line 245)
  - Normalization: Sets `UiState` to `Offline` if `!peer.IsOnline` (lines 591-599)
  - **Removal Impact:** Remove `isOnline` parameter from constructor call, remove assignment to DTO, remove normalization logic

- **SimulatorState.cs** (Line 26)
  - DTO property: `public bool IsOnline { get; set; } = true;` (line 26)
  - **Removal Impact:** Remove property from DTO

- **PeerStateSnapshot.cs** (Line 13)
  - Snapshot record parameter: `bool IsOnline` (line 13)
  - **Removal Impact:** Remove parameter from record

**1.5 Test Layer**
- **SimulatedPeerRuntimeFinalizeRelayedTests.cs** (Lines 91, 99)
  - Constructor calls with `isOnline: true` (2 occurrences)
  - **Removal Impact:** Remove `isOnline` parameter from SimulatedPeerModel constructor calls

- **SimulatedHandshakeStateMachineCardViewModelDiagnosticsTests.cs** (Lines 38, 49, 84, 96)
  - Constructor calls with `isOnline: true` (4 occurrences)
  - **Removal Impact:** Remove `isOnline` parameter from SimulatedPeerModel constructor calls

- **SimulatedPeerDirectoryInitializationTests.cs** (Line 29)
  - Constructor call with `isOnline: true`
  - **Removal Impact:** Remove `isOnline` parameter from SimulatedPeerModel constructor call

- **SimulatedPeerRuntimeServiceDecryptFailureDiagnosticsTests.cs** (Line 75)
  - Constructor call with `isOnline: true`
  - **Removal Impact:** Remove `isOnline` parameter from SimulatedPeerModel constructor call

- **SimulatedPeerRuntimeFinalizeTests.cs** (Lines 89, 97, 194, 202)
  - Constructor calls with `isOnline: true` (4 occurrences)
  - **Removal Impact:** Remove `isOnline` parameter from SimulatedPeerModel constructor calls

- **SimulatedPeerRuntimeStandardHandshakeRelayedTests.cs** (Lines 127, 197, 244, 339, 347, 427, 435, 527, 535, 543, 697, 793)
  - Constructor calls with `isOnline: true` (12 occurrences)
  - **Removal Impact:** Remove `isOnline` parameter from SimulatedPeerModel constructor calls

- **SimulatorDiagnosticBundleBuilderTests.cs** (Lines 37, 38)
  - Constructor calls with `isOnline: true` (2 occurrences)
  - **Removal Impact:** Remove `isOnline` parameter from SimulatedPeerModel constructor calls

- **SimulatorDiagnosticsTabViewModelFilteringTests.cs** (Lines 38, 39, 40)
  - Constructor calls with `isOnline: true` (3 occurrences)
  - **Removal Impact:** Remove `isOnline` parameter from SimulatedPeerModel constructor calls

- **SimulatorStateServiceInitializationTests.cs** (Line 102)
  - Constructor call with `isOnline: true`
  - **Removal Impact:** Remove `isOnline` parameter from SimulatedPeerModel constructor call

- **SimulatorStateStoreTests.cs** (Line 57)
  - Constructor call with `isOnline: true`
  - **Removal Impact:** Remove `isOnline` parameter from SimulatedPeerModel constructor call

**Total:** 31 test constructor calls with `isOnline` parameter across 10 test files. All need the parameter removed.

**1.6 Business Logic**
- **SimulatedPeerRuntimeTracker.cs** (Line 16)
  - Subscribes to `peer.IsOnline` to trigger save operations: `peer.IsOnline.Subscribe(_ => _dirty.OnNext(Unit.Default))`
  - **Removal Impact:** Remove this subscription - changes to IsOnline will no longer trigger saves (which is correct since IsOnline will be removed)

- **SimulatorStateService.cs** (Lines 2158, 2311)
  - `CreatePeerFromSnapshot`: Passes `isOnline: snap.IsOnline` to constructor (line 2158)
  - `AddSimulatedPeerAsync`: Passes `isOnline: true` to constructor (line 2311)
  - **Removal Impact:** Remove `isOnline` parameter from constructor calls

- **SimulatorDiagnosticBundleBuilder.cs** (Line 37)
  - Reads `IsOnline = p.IsOnline.CurrentValue` for diagnostic bundle export
  - **Removal Impact:** Remove this line from diagnostic bundle export

**Finding:** No routing logic, message queuing, or relay orchestration depends on IsOnline status. The only business logic dependencies are in simulator-specific code (state service, runtime tracker, diagnostic bundle builder).

**1.7 Reachability Research**
- **Percolator.Network.ValueObjects.Reachability.cs** (Lines 3-9)
  - Enum `ReachabilityStatus` with values: Unknown, Online, Offline, Degraded
  - Class `Reachability` tracks status and last change timestamp

- **PeerRoutingProfile.cs** (Line 10)
  - Property `public Reachability Reachability { get; private set; } = new();`
  - Method `RecordReachability(ReachabilityStatus status, DateTimeOffset now)`

- **RoutePlanner.cs** (Line 22)
  - Logic: `if (profile.Reachability.Status != ReachabilityStatus.Offline)` - excludes endpoints when offline
  - Uses `ReachabilityPolicy` for routing decisions

- **Infrastructure Layer**
  - `SqlitePeerRoutingProfileRepository.cs` - persists reachability status to database
  - `PeerRoutingProfileDbo.cs` - DTO with `ReachabilityStatus` and `ReachabilityLastChangeUtc`
  - Database migrations include reachability columns

**Finding:** `Reachability` is a network-layer concept used for routing decisions. It tracks whether a peer's endpoints are reachable (can be contacted) based on network observations. This is distinct from the simulator's `IsOnline` which is a UI/simulator state toggle. The "Online" value in `ReachabilityStatus` represents network reachability (peer can be contacted), not social presence.

**Recommendation:** Keep `Reachability` as-is. It serves a legitimate network routing purpose and is not part of the "online/offline" social presence concept that Signal rejects. The naming overlap is unfortunate but the concepts are functionally distinct.

---

### Phase 2: Categorize References

**2.1 Display-Only References**
- **InitialsAvatar.xaml.cs** (Lines 23-30) - Dependency property
- **InitialsAvatar.xaml** (Lines 15-17) - Presence dot visualization
- **ChatView.xaml** (Line 30) - Binding to IsOnline
- **SessionsSidebarView.xaml** (Line 54) - Binding to IsOnline
- **ChatViewModel.cs** (Lines 22, 57, 124) - Property, initialization, disposal
- **SimulatedPeerCardViewModel.cs** (Lines 51-53, 55, 86-92, 164, 409) - Property, binding, commands
- **SimulatedPeerItemViewModel.cs** (Lines 101-105, 211) - ToggleOnlineCommand
- **SessionContext.cs** (Line 12) - Property
- **SessionListItem.cs** (Line 17) - Property

**Note:** PeerConnectionListItemViewModel.cs IsOnline is derived from connection status (Direct/Relay/Group), not online/offline. Decision: RENAME property to `IsConnectionEstablished` to accurately reflect its meaning.

**2.2 Persistence References**
- **JsonSimulatorStateRepository.cs** (Lines 194, 245, 591-599) - Serialization/deserialization
- **SimulatorState.cs** (Line 26) - DTO property
- **PeerStateSnapshot.cs** (Line 13) - Snapshot record parameter

**2.3 Business Logic References**
- **SimulatedPeerRuntimeTracker.cs** (Line 16) - Subscription to trigger saves
- **SimulatorStateService.cs** (Lines 2158, 2311) - Constructor calls
- **SimulatorDiagnosticBundleBuilder.cs** (Line 37) - Diagnostic bundle export
- **SimulatedPeerModel.cs** (Lines 55, 85, 90-98, 146, 296, 373, 483) - Core model property and logic
- **PeerConnectionListItemViewModel.cs** (Lines 18, 48-53) - Property to be renamed to IsConnectionEstablished

**2.4 Test References**
- 10 test files with 31 SimulatedPeerModel constructor calls using `isOnline` parameter
- All need parameter removal

---

### Phase 3: Determine Removal Strategy

**3.1 Display Removal**
- Remove `IsOnline` dependency property from `InitialsAvatar.xaml.cs` (lines 23-30)
- Remove `IsOnline` binding from `InitialsAvatar.xaml` (line 17) - keep the ellipse for future repurposing as notification indicator
- Remove `IsOnline` bindings from `ChatView.xaml` (line 30) and `SessionsSidebarView.xaml` (line 54)
- Remove `IsOnline` property from `ChatViewModel.cs` (lines 22, 57, 124)
- Remove `IsOnline` property from `SessionListItem.cs` (line 17)
- Remove `IsOnline` property from `SessionContext.cs` (line 12)
- Remove `IsOnline` property from `SimulatedPeerCardViewModel.cs` (lines 51-53, 55, 86-92, 164, 409)
- Remove `ToggleOnlineCommand` and `TogglePowerCommand` from `SimulatedPeerCardViewModel.cs` and remove toggle buttons from XAML
- Remove `ToggleOnlineCommand` from `SimulatedPeerItemViewModel.cs` (lines 101-105, 211) and remove toggle button from XAML

**Presence Indicator Replacement:** The ellipse in InitialsAvatar will be repurposed in a separate chunk as a notification indicator for unread changes (e.g., unread messages). For this chunk, just remove the IsOnline binding.

**3.2 Persistence Removal**
- Remove `IsOnline` property from `SimulatedPeerDto` in `SimulatorState.cs` (line 26)
- Remove `IsOnline` parameter from `PeerStateSnapshot` record in `PeerStateSnapshot.cs` (line 13)
- Remove `isOnline: dto.IsOnline` from `CreatePeerSnapshot` in `JsonSimulatorStateRepository.cs` (line 194)
- Remove `IsOnline = model.IsOnline` from `CreateDto` in `JsonSimulatorStateRepository.cs` (line 245)
- Remove normalization logic that sets `UiState` based on `IsOnline` in `JsonSimulatorStateRepository.cs` (lines 591-599)

**State Migration:** Existing simulator state files will have `IsOnline` field. Deserialization should ignore this field (JSON deserialization will handle this automatically if the property is removed). No explicit migration needed.

**3.3 Business Logic Removal**
- Remove `isOnline` parameter from `SimulatedPeerModel` constructor (line 55)
- Remove `IsOnline` property initialization (line 85)
- Remove logic that sets `UiState` based on `isOnline` (lines 90-98) - REPLACE with default `UiState = SimulatorPeerUiState.Ready`
- Remove `IsOnline` property declaration (line 146)
- Remove `IsOnline` usage in `ClearRuntimeState` (line 296) - REPLACE with `_uiState.Value = SimulatorPeerUiState.Ready`
- Remove `IsOnline` from `Freeze` snapshot (line 373)
- Remove `IsOnline.Dispose()` (line 483)
- Remove `peer.IsOnline.Subscribe(...)` from `SimulatedPeerRuntimeTracker.cs` (line 16)
- Remove `isOnline: snap.IsOnline` from `CreatePeerFromSnapshot` in `SimulatorStateService.cs` (line 2158)
- Remove `isOnline: true` from `AddSimulatedPeerAsync` in `SimulatorStateService.cs` (line 2311)
- Remove `IsOnline = p.IsOnline.CurrentValue` from `SimulatorDiagnosticBundleBuilder.cs` (line 37)

**3.4 Rename PeerConnectionListItemViewModel.cs Property**
- Rename `IsOnline` property to `IsConnectionEstablished` in `PeerConnectionListItemViewModel.cs` (line 18)
- Update property name in any XAML bindings that reference this property
- This property is derived from connection status (Direct/Relay/Group) and should be kept with an accurate name

**3.5 Test Removal**
- Remove `isOnline` parameter from all 31 `SimulatedPeerModel` constructor calls across 10 test files
- No test logic currently asserts on IsOnline status, so no assertion removal needed

---

### Phase 4: Implementation Order

**Order Rationale:** Remove from outside-in to avoid breaking dependencies. Start with UI (safest), then persistence, then business logic core, then tests.

**Step 1: Remove from UI Layer** (safest, no functional impact)
- Remove `IsOnline` dependency property from `InitialsAvatar.xaml.cs`
- Remove `IsOnline` binding from `InitialsAvatar.xaml` (keep ellipse for future repurposing as notification indicator)
- Remove `IsOnline` bindings from `ChatView.xaml` and `SessionsSidebarView.xaml`
- Remove `IsOnline` property from `ChatViewModel.cs`
- Remove `IsOnline` property from `SessionListItem.cs`
- Remove `IsOnline` property from `SessionContext.cs`
- Remove `IsOnline` property from `SimulatedPeerCardViewModel.cs`
- Remove `ToggleOnlineCommand` and `TogglePowerCommand` from `SimulatedPeerCardViewModel.cs` and remove toggle buttons from XAML
- Remove `ToggleOnlineCommand` from `SimulatedPeerItemViewModel.cs` and remove toggle button from XAML

**Step 2: Remove from Persistence Layer**
- Remove `IsOnline` property from `SimulatedPeerDto` in `SimulatorState.cs`
- Remove `IsOnline` parameter from `PeerStateSnapshot` record in `PeerStateSnapshot.cs`
- Remove `isOnline` usage from `JsonSimulatorStateRepository.cs` (3 locations)
- Remove normalization logic that sets `UiState` based on `IsOnline`

**Step 3: Remove from Business Logic Core**
- Remove `isOnline` parameter from `SimulatedPeerModel` constructor
- Remove `IsOnline` property and all usages in `SimulatedPeerModel.cs` (7 locations)
- Replace logic that sets `UiState` based on `isOnline` with default `UiState = SimulatorPeerUiState.Ready`
- Replace `ClearRuntimeState` logic to set `_uiState.Value = SimulatorPeerUiState.Ready` instead of using IsOnline
- Remove `peer.IsOnline.Subscribe(...)` from `SimulatedPeerRuntimeTracker.cs`
- Remove `isOnline` usage from `SimulatorStateService.cs` (2 locations)
- Remove `IsOnline` usage from `SimulatorDiagnosticBundleBuilder.cs`

**Step 4: Rename PeerConnectionListItemViewModel.cs Property**
- Rename `IsOnline` property to `IsConnectionEstablished` in `PeerConnectionListItemViewModel.cs`
- Update any XAML bindings that reference this property

**Step 5: Update Tests**
- Remove `isOnline` parameter from all 31 `SimulatedPeerModel` constructor calls across 10 test files

**Step 6: Verify**
- Build solution
- Run all tests
- Verify simulator state files load correctly (ignoring old `IsOnline` field)

---

### Phase 5: Verification

**5.1 Build Verification**
- Build entire solution
- Fix any compilation errors
- Ensure no references to removed properties remain

**5.2 Test Verification**
- Run all unit tests
- Run all integration tests
- Verify all tests pass
- Verify no tests fail due to missing `IsOnline` parameter

**5.3 Runtime Verification**
- Start application
- Verify simulator loads correctly
- Verify simulator state files with old `IsOnline` field load correctly (JSON deserialization should ignore missing field)
- Verify UI renders without presence dots (ellipse is present but not bound to IsOnline)
- Verify ToggleOnlineCommand buttons are removed from XAML
- Verify all peers default to `SimulatorPeerUiState.Ready` after loading

**5.4 State Migration Verification**
- Create a test state file with `IsOnline` field
- Load the state file after removal
- Verify it loads without errors
- Verify it saves without `IsOnline` field

---

### Open Questions Resolved

**Q1: State Migration**
- **Answer:** No explicit migration needed. JSON deserialization will automatically ignore the removed `IsOnline` field when loading old state files. New state files will not contain the field.

**Q2: Presence Indicator Meaning**
- **Answer:** The ellipse in InitialsAvatar will be repurposed in a separate chunk as a notification indicator for unread changes (e.g., unread messages). For this chunk, just remove the IsOnline binding and keep the ellipse for future use.

**Q3: Simulator UX Toggle**
- **Answer:** Remove ToggleOnlineCommand and TogglePowerCommand from ViewModels and remove toggle buttons from XAML entirely. Commands are being removed, not made into no-ops.

**Q4: Reachability Concept**
- **Answer:** `Reachability` is a network-layer concept used for routing decisions (whether peer endpoints are contactable). It is distinct from the simulator's `IsOnline` (UI state toggle). Keep `Reachability` as-is.

**Q5: UiState After IsOnline Removal**
- **Answer:** Default all peers to `SimulatorPeerUiState.Ready` after IsOnline removal. Replace logic that sets UiState based on IsOnline with this default value.

**Q6: PeerConnectionListItemViewModel.cs Property**
- **Answer:** Rename `IsOnline` property to `IsConnectionEstablished` to accurately reflect its meaning (derived from connection status Direct/Relay/Group). Update any XAML bindings.

---
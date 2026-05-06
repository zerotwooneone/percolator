## Chunk A

### Problem

Chat messages sent from a relayed simulator peer show up in the main window immediately rather than going into the simulator-outbound relay queue.

### Related Code Paths

**Routing Logic:**
- `Desktop.Wpf.Features.Simulator.SimulatorStateService.SendChatMessageToMainAsync` (line 1939)
  - Checks `model.ConnectionMode.CurrentValue == ConnectionMode.ViaRelay`
  - If true: enqueues to relay queue via `EnqueueRelayUpstreamToMainAsync`
  - If false: sends directly via `PercolatorMessageService` (bypasses relay queue)

**Handshake State Machine:**
- `Desktop.Wpf.Features.Simulator.SimulatedHandshakeStateMachineCardViewModel.cs` (line 296)
  - When initiating relay handshake: `_model.SetSelectedRouteMode(ConnectionMode.ViaRelay)`
  - Also sets relay host via `_model.SetRelayHostPeerId(new PeerId(relayHost.PeerId.Value))`

**Outbound Handshake Completion (simulator -> main):**
- `Desktop.Wpf.Features.Simulator.SimulatorStateService.ReceiveRelayedOpaquePayloadAsync` (line 1827)
  - Handles `EstablishSessionResponse` from main
  - Establishes final session from temporary session
  - Calls `model.ClearPendingStandardHandshakeToMain()` to clear pending state
  - Does NOT call `model.SetConnection(...)` to update connection mode

**Inbound Handshake Acceptance (main -> simulator):**
- `Desktop.Wpf.Features.Simulator.SimulatorStateService.TryAcceptPendingStandardSignalHelloAsync` (line 1042)
  - Called when user accepts a pending relayed handshake hello from main
  - Retrieves pending hello which includes `relayHostPeerId` (line 1064)
  - Calls `ReceiveEstablishSessionFromMainAsync` to establish the session (line 1100)
  - Sends response back via relay
- `Desktop.Wpf.Features.Simulator.SimulatorStateService.ReceiveEstablishSessionFromMainAsync` (line 473)
  - Establishes session from main's handshake request
  - Adds session to `model.SessionsMutable` (line 553)
  - Does NOT call `model.SetConnection(...)` to update connection mode

**Property Definitions:**
- `Desktop.Wpf.Features.Simulator.Models.SimulatedPeerModel.cs`
  - `_connectionMode` - `ReactiveProperty<ConnectionMode>` (non-nullable, defaults to `Direct`)
  - `_relayPeerId` - set by `SetConnection(..., relayPeerId)` (connection routing state)
  - `_relayHostPeerId` - set by `SetRelayHostPeerId(...)` (handshake attempt / UI state)
  - `SetConnection` sets `_connectionMode`, `_host`, `_port`, and `_relayPeerId`
  - `RelayHostPeerId` is NOT cleared by `ClearPendingStandardHandshakeToMain()`, only by `ClearRuntimeState()`

### Root Cause

The chat send path routes to the simulator relay queue only when:
- `model.ConnectionMode.CurrentValue == ConnectionMode.ViaRelay`
- `model.RelayPeerId.CurrentValue != Guid.Empty`

When a relay handshake is initiated, the state machine sets:
- `SelectedRouteMode = ViaRelay` (handshake attempt configuration)
- `RelayHostPeerId = <relay host>` (handshake attempt configuration)

But when the handshake completes successfully (both outbound and inbound paths), the code never calls `SetConnection(...)` to update the actual connection state. As a result:
- `ConnectionMode` remains `Direct` (default value)
- `RelayPeerId` remains `Guid.Empty` (default value)

Chat messages therefore take the direct delivery path (`PercolatorMessageService.DeliverOpaqueMessage(...)`) and show up in the main window immediately.

**This affects both handshake directions:**
- Outbound (simulator -> main via relay): Handshake completion in `ReceiveRelayedOpaquePayloadAsync`
- Inbound (main -> simulator via relay): Handshake acceptance in `ReceiveEstablishSessionFromMainAsync`

### Confirmed Symptom

At breakpoint in `SendChatMessageToMainAsync` line 1939, `model.ConnectionMode.CurrentValue == Direct` even when the peer was configured for relay handshake.

### Solution: Set ConnectionMode at Relay Handshake Completion

**Key insight:** `RelayHostPeerId` is set during handshake initiation and is NOT cleared by `ClearPendingStandardHandshakeToMain()`. It's only cleared by `ClearRuntimeState()`. This means the relay host peer ID is already available at handshake completion without adding new fields.

**Implementation (Outbound path - simulator -> main):**

Modify `Desktop.Wpf.Features.Simulator.SimulatorStateService.ReceiveRelayedOpaquePayloadAsync` (around line 1827):

```csharp
// After session is established (line 1825: model.SessionsMutable[final.Id] = final;)

// Check if this was a relay handshake by looking at RelayHostPeerId
var relayHostPeerId = model.RelayHostPeerId.CurrentValue;
if (relayHostPeerId is not null && relayHostPeerId.Value != Guid.Empty)
{
    // Set connection mode to ViaRelay with the relay host peer ID
    // Preserve existing Host and Port values
    model.SetConnection(
        ConnectionMode.ViaRelay,
        host: model.Host.CurrentValue,
        port: model.Port.CurrentValue,
        relayPeerId: relayHostPeerId.Value);
}

// Then clear pending state
model.ClearPendingStandardHandshakeToMain();
```

**Implementation (Inbound path - main -> simulator):**

Modify `Desktop.Wpf.Features.Simulator.SimulatorStateService.TryAcceptPendingStandardSignalHelloAsync` (after line 1100, after calling `ReceiveEstablishSessionFromMainAsync`):

```csharp
// After calling ReceiveEstablishSessionFromMainAsync (line 1100)

// Check if this was a relay handshake (relayHostPeerId is from pending hello)
if (relayHostPeerId.Value != Guid.Empty)
{
    var model = _peers.FirstOrDefault(p => p.PeerId == recipientPeerId);
    if (model is not null)
    {
        // Set connection mode to ViaRelay with the relay host peer ID
        // Preserve existing Host and Port values
        model.SetConnection(
            ConnectionMode.ViaRelay,
            host: model.Host.CurrentValue,
            port: model.Port.CurrentValue,
            relayPeerId: relayHostPeerId);
    }
}
```

**Why this works:**
- Outbound path: `RelayHostPeerId` is set during handshake initiation and persists through handshake completion
- Inbound path: `relayHostPeerId` is retrieved from the pending hello and is available after session establishment
- Setting `ConnectionMode` to `ViaRelay` and `RelayPeerId` to the relay host peer ID satisfies the routing logic conditions
- Chat messages will then be enqueued via `EnqueueRelayUpstreamToMainAsync` instead of being delivered directly
- Preserving existing `Host` and `Port` values avoids breaking other flows that rely on these values

**Side effects to verify:**

1. **Persistence trigger:** `SimulatedPeerRuntimeTracker` subscribes to `ConnectionMode` and `RelayPeerId` changes (lines 19, 22). Setting these values will trigger a save operation. This is expected and likely desired behavior.

2. **Asymmetry with direct handshakes:** The direct handshake path (`ReceiveEstablishSessionFromMainAsync`, line 473) does NOT call `SetConnection` either. Direct handshakes will keep the default `ConnectionMode.Direct` value, which is semantically correct. However, this creates an asymmetry:
   - Relay handshakes: Explicitly set `ConnectionMode.ViaRelay` and `RelayPeerId`
   - Direct handshakes: Keep default `ConnectionMode.Direct` and empty `RelayPeerId`
   - This asymmetry is acceptable since the defaults are correct for direct connections

3. **Diagnostic bundle export:** `SimulatorDiagnosticBundleBuilder` reads `ConnectionMode` and `RelayPeerId` (lines 37-38) for export. The change will cause relayed peers to show the correct connection mode in diagnostic bundles.

4. **No other callers of SetConnection:** `SetConnection` is only defined in `SimulatedPeerModel` and has no other callers in the simulator code. This suggests it's not currently used elsewhere, so changing it won't affect other flows.

5. **Switching between direct and relay:** If a user switches from relay to direct handshake, `RelayHostPeerId` will be cleared (set to null) by the handshake state machine. The next direct handshake won't call `SetConnection`, so `ConnectionMode` will remain `ViaRelay` and `RelayPeerId` will remain set. This is acceptable - the routing logic checks both `ConnectionMode` and `RelayPeerId`, and if the user initiates a direct handshake, they are explicitly choosing direct communication regardless of the previous connection state.

6. **Inbound path correctness:** The inbound path (main -> relay -> simulator) is handled by `TryAcceptPendingStandardSignalHelloAsync`. This method is only called for relayed handshakes (direct handshakes bypass the pending hello mechanism and go directly to `ReceiveEstablishSessionFromMainAsync`). Therefore, checking `relayHostPeerId.Value != Guid.Empty` is the correct way to distinguish relayed from direct handshakes in this context.

### All Places Where RelayPeerId is Set

**Runtime mutations (SetConnection calls):**
1. `TryAcceptPendingStandardSignalHelloAsync` (line 1124) - Sets both `ConnectionMode.ViaRelay` and `RelayPeerId` - **Already correct**
2. `ReceiveRelayedOpaquePayloadAsync` (line 1857) - Sets both `ConnectionMode.ViaRelay` and `RelayPeerId` - **Already correct**

**Constructor initialization:**
3. `SimulatedPeerModel` constructor (line 62) - Takes `relayPeerId` parameter and `connectionMode` parameter separately - **Caller responsibility to ensure consistency**
4. `JsonSimulatorStateRepository.LoadStateAsync` (line 201) - Loads from DTO, passes both `relayPeerId` and `connectionMode` from DTO - **DTO should be consistent**
5. `SimulatorStateService.CreatePeerFromSnapshot` (line 2160) - Creates from snapshot, passes both `relayPeerId` and `connectionMode` from snapshot - **Snapshot should be consistent**
6. `SimulatorStateService.AddSimulatedPeerAsync` (line 2313) - Creates new peer with `relayPeerId: null` and `connectionMode: Direct` - **Already consistent**

**Conclusion:** All places where `RelayPeerId` is set either already set `ConnectionMode` to `ViaRelay` correctly (the two handshake completion fixes), or they are loading from persisted state where both values should already be consistent. No additional changes needed.

### Deadlock Fix in SendChatMessageToMainAsync

**Problem:** `SendChatMessageToMainAsync` acquires `_stateGate`, then calls `EnqueueRelayUpstreamToMainAsync` which also tries to acquire `_stateGate`, causing a deadlock.

**Current Solution (Implemented):**
1. Acquire state gate
2. Capture necessary values (relayHostPeerId, cipherBytes, routing decision) while holding gate
3. Release state gate
4. Perform routing (call `EnqueueRelayUpstreamToMainAsync` or direct delivery) outside gate

**Alternative Approach (Not Implemented):**
Create a private version `EnqueueRelayUpstreamToMainAsyncCore` that assumes the caller already holds the lock:
1. Acquire state gate
2. Call private version while still holding gate
3. Private version does not acquire lock itself

**Comparison:**

| Aspect | Current (Capture, Release, Call) | Alternative (Private Locked Version) |
|--------|----------------------------------|--------------------------------------|
| Lock scope | Minimal - only for state operations | Larger - held during async operation |
| Deadlock risk | Low - lock released before async call | High - lock held during async, risk of nested acquisitions |
| Race conditions | Possible if state changes between capture and use | None - direct access to state |
| Code complexity | Slightly more verbose (capture variables) | Less verbose in caller, but needs new private method |
| Maintainability | Clear lock boundaries, easier to reason about | Less clear lock boundaries, harder to track lock state |
| Async safety | Good - lock not held across await | Poor - lock held across await (anti-pattern) |

**Recommendation:** Keep current approach. Holding a lock across an async operation is an anti-pattern that can lead to deadlocks and reduces concurrency. The current approach is safer and follows best practices.
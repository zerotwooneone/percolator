# Desktop WPF Simulator Window — UI/UX Flows and Requirements

## Purpose
The Simulator window is a deterministic, inspectable environment for validating Percolator’s peer setup, handshake state machines, relay transport behavior, and Double Ratchet encrypted messaging flows.

This document specifies the required user flows, UI expectations, and state rules to validate both Direct P2P and Relayed E2EE protocols. It explicitly defines how the Main App interacts with the Simulator through an explicit, interceptable Relay Queue.

## Key Terms
- **Main Node**: The running Desktop app instance (the local user).
- **Simulated Peer**: A peer runtime managed inside the Simulator.
- **Relay Node**: A Simulated Peer explicitly flagged as a router. In 1:1 sessions, it intercepts, queues, and forwards opaque payloads. In Group sessions, it validates ZK proofs and manages blinded routing fanout.
- **Handshake State Machine**: The explicit lifecycle of a peer connection (`No Handshake`, `Request Sent`, `Request Received`, `Handshake Complete`, `Expired`).
- **Public Identity (PKH)**: The cryptographic public identifier used to address peers.
- **Out-of-Band (OOB) Token**: A shareable Base64 string representing an invitation to initiate a handshake.
- **Zero-Knowledge (ZK) Proof**: A cryptographic proof attached to group messages, allowing a sender to prove group membership without revealing their identity to the relay.
- **Blinded Routing Table**: A table maintained on Relay Nodes used to fan out group messages without the relay knowing the plaintext social graph of the group.

## High-Level Requirements
- **R1 — Transport Interception**: All protocol traffic between the Main Node and a Simulated Peer MUST pass through the Simulator's Relay Queue, allowing the user to pause, inspect, reorder, or drop payloads.
- **R2 — Explicit State Machines**: Handshakes must not auto-complete via hidden background timers. The UI must expose the exact current state of a peer's handshake and require explicit user action to advance it.
- **R3 — Cryptographic Inspectability**: The simulator must visualize the active Double Ratchet state (Root Hash, Chain Key Index, Skipped Keys) for all successful sessions.
- **R4 — Separation of Concerns**: Transport (Relay Tab), Identity (Peers Tab), and Cryptography (Sessions Tab) must be distinctly separated to allow granular debugging.

## Information Architecture
The Simulator provides five distinct control panels:
1. **Peers**: Identity creation, PKH management, pre-key hosting/publishing, and inline messaging.
2. **Handshakes**: State machine visualization and manual progression controls.
3. **Relay**: Per-relay network queues, auto-delivery toggles, and manual payload routing.
4. **Sessions**: Cryptographic state inspection (Direct Ratchets, ZK Proof status, & Group Epochs).
5. **Diagnostics**: Chronological system events and error logs.

## Required Workflows

### Flow 1 — Manage Simulated Peers
**Goal:** Create and configure actors on the network.
1. User navigates to **Peers** tab and clicks **Add Peer**.
2. Simulator generates a new identity (Name, PKH, Endpoint).
3. User toggles **Online/Offline** power state to test reachability.
4. User toggles **Enable Relay** to designate the peer as network infrastructure, adding them to the Relay Tab.
5. If the peer is Online and its Handshake State is `Handshake Complete`, an inline message input appears allowing the peer to send messages directly to the Main Node.

### Flow 2 — Many-to-Many Pre-Key Distribution
**Goal:** Establish directory hosting relationships for initial key exchange.
1. In the **Peers** tab, User locates Peer A.
2. User selects Peer B from the "Publish keys to..." dropdown. A peer can publish keys to multiple other peers.
3. Simulator UI updates:
  - Peer A displays a tag: `Published to: Peer B`.
  - Peer B displays a tag: `Hosting keys for: Peer A`.
4. User clicks the **"X"** on any tag to instantly revoke/delete the hosted keys for that relationship.

### Flow 3 — Handshake Path A: Main Node Initiates (Search & Connect)
**Goal:** Validate standard X3DH initiation initiated by the local user.
1. User copies Peer A's **PKH** from the Simulator.
2. User opens the Main Node's "New Handshake" modal, pastes the PKH, and clicks **Search & Connect**.
3. Main Node generates an `X3DH_INIT` payload and pushes it to the Simulator's **Relay Queue**.
4. User navigates to the **Relay** tab and clicks **Deliver** on the payload.
5. Peer A's state in the **Handshakes** tab changes from `No Handshake` to `Request Received`.
6. User clicks **Accept Handshake** on Peer A in the Simulator. Peer A queues an `X3DH_ACCEPT` payload in the Relay.
7. User navigates to the **Relay** tab and clicks **Deliver**.
8. Main Node receives the ACCEPT, session is established, and the Chat UI opens. Peer A's state updates to `Handshake Complete`.

### Flow 4 — Handshake Path B: Simulated Peer Initiates (Reverse Request)
**Goal:** Validate the Main Node's ability to receive and accept incoming handshake requests.
1. In the Simulator's **Handshakes** tab, User clicks **Send Request to Main App** on Peer A.
2. Peer A's state changes from `No Handshake` to `Request Sent`. An `X3DH_INIT` payload is queued in the Relay.
3. User navigates to the **Relay** tab and clicks **Deliver**.
4. The Main Node's `UserPlus` (Incoming Signals) icon begins to pulse.
5. User clicks the icon on the Main Node and clicks **Accept**.
6. Main Node queues an `X3DH_ACCEPT` payload in the Relay.
7. User navigates to the **Relay** tab and clicks **Deliver**.
8. Peer A's state changes to `Handshake Complete`.

### Flow 5 — Handshake Path C: Out-of-Band (OOB) Token
**Goal:** Validate offline style invitations.
1. In the Simulator, User clicks **Copy Invite Token** for Peer A.
2. User opens Main Node, navigates to "Import Token", pastes the Base64 string, and clicks **Decode & Initiate**.
3. Main Node queues an `X3DH_INIT` payload in the Relay.
4. Flow proceeds exactly as *Handshake Path A (Step 4)*.

### Flow 6 — Handshake State Machine Management
**Goal:** Allow users to force state transitions for testing.
1. From the **Handshakes** tab, User can view the explicit state of any peer (`No Handshake`, `Request Sent`, `Request Received`, `Handshake Complete`, `Expired`).
2. If a peer is in `Request Sent`, User can click **Force Expire** to transition the state to `Expired`.
3. If a peer is in `Handshake Complete` or `Expired`, User can click **Reset State** to transition back to `No Handshake`.

### Flow 7 — Outbound Messaging (Main -> Simulator)
**Goal:** Validate standard payload encryption and ratchet progression.
1. Session is established (`Handshake Complete`).
2. User types a message in the Main Node Chat UI and presses Enter.
3. Main Node UI optimistically displays the message locally.
4. A `MESSAGE` payload is queued in the **Relay** tab.
5. User clicks **Deliver** on the payload.
6. In the **Sessions** tab, the Chain Index for the session instantly increments.

### Flow 8 — Inbound Messaging (Simulator -> Main)
**Goal:** Validate the Main Node's ability to receive and decrypt payloads.
1. In the **Peers** tab, User locates a peer with a `Handshake Complete` status.
2. An inline message input is visible. User types a message and clicks **Send**.
3. A `MESSAGE` payload is queued in the **Relay** tab.
4. User clicks **Deliver**.
5. The Main Node UI updates with the incoming message and read receipts. The Ratchet Chain Index increments in the **Sessions** tab.

### Flow 9 — Relay Queue Management & Automation
**Goal:** Control network flow for rapid testing vs. granular debugging.
1. **Manual Mode (Default):** Payloads sit in a specific Relay's queue indefinitely until the User explicitly clicks **Deliver** on that individual payload.
2. **Dequeue Next (Per Relay):** User clicks the **Next** button on a relay to instantly process the oldest payload in that specific queue.
3. **Dequeue All (Per Relay):** User clicks the **All** button on a relay to instantly process all currently queued payloads for that relay.
4. **Auto-Deliver (Per Relay):** User toggles the "Auto-Deliver" checkbox on a specific Relay Node. Payloads routed through this node will auto-deliver.
5. **Global Auto-Relay:** User toggles the "Global Auto-Relay All" checkbox at the top of the Relay Tab. All payloads across all relays instantly auto-deliver as long as this is checked.

### Flow 10 — Fault Injection (Out-of-Order Delivery)
**Goal:** Validate protocol resilience to network unreliability.
1. Auto-Relay is turned OFF.
2. User sends Message 1, then Message 2 from the Main Node. Both queue in the Relay.
3. User clicks **Deliver** on Message 2 *first*.
4. **Expected Outcome:** Message 2 successfully decrypts. The **Sessions** tab updates to show `1 stored` under the "Skipped Keys" metric.
5. User clicks **Deliver** on Message 1. Message 1 decrypts using the stored skipped key, and the metric returns to `0`.

### Group Messaging (Bi-directional Requirement)
**Important:** All group operations (Creation, Messaging, Admin Modifications, and Leaving) must be executable from BOTH the Main Node UI and the Simulated Peers UI. The flows below use the Main Node as the initiator for brevity, but the simulator must provide UI controls for Simulated Peers to initiate these exact same actions, generating the inverse inbound payload flows.

### Flow 11 — Hybrid ZK-Relay Group Creation
**Goal:** Validate the establishment of a ZK-based Group Session and blinded routing over 1:1 transport.
1. Main Node establishes 1:1 `Handshake Complete` sessions with Simulator Peer A, Peer B, and a designated Relay Node R (Relay R could be one of the peers, or a dedicated router).
2. In the Main Node UI, User creates a new Group, selecting Peer A, Peer B, and designating Relay Node R as the router. *(Note: A Simulated Peer could also initiate this via the Simulator UI).*
3. Initiator queues opaque `MESSAGE` payloads containing ZK routing configuration (blinding tokens/Group Admin commands) to Relay R.
4. User navigates to the **Relay** tab in the Simulator and clicks **Next** (or **Deliver**) on Relay R's queue to process the setup commands.
5. Relay R updates its internal state to reflect the blinded routing tokens for the new group.
6. Initiator generates and queues `MESSAGE` payloads (containing the actual ZK Group Invitation) intended for Peer A and Peer B, routed via Relay R.
7. User clicks **Deliver** on these payloads in Relay R's queue. Relay R performs "Fanout" actions based on the blinded routing table, generating new downstream payloads aimed at Peer A and Peer B.
8. User clicks **Deliver** on the fanned-out payloads. Simulator **Sessions** tab displays a `GROUP` session card reflecting the current Group Epoch.

### Flow 12 — Hybrid ZK-Relay Group Messaging
**Goal:** Validate ZK Proof validation and Relay Fanout.
1. Group Session is established (Epoch > 0).
2. User types a group message in the Main Node Chat UI and presses Enter. *(Or via a Simulated Peer's inline chat).*
3. Sender generates a ZK Proof of Membership and encrypts the payload.
4. Sender pushes a standard 1:1 `MESSAGE` payload (wrapping the ZK Group Message) to the **Relay Queue** for Relay R.
5. User clicks **Next** (or **Deliver**) on the payload in Relay R's queue.
6. Relay R intercepts the payload, strips the 1:1 transport identity, validates the ZK proof, and performs a "Fanout" action based on its blinded routing table. 
7. Relay R queues new `MESSAGE` payloads addressed to the destination endpoints of the group members.
8. User clicks **Deliver** on each fanned-out payload.
9. Receiving peers validate the ZK proof locally and decrypt the message. The Group Ratchet state updates in the **Sessions** tab.

### Flow 13 — Group Admin Operations (Epoch Rotation)
**Goal:** Validate Group Master Key rotation and routing table updates.
1. User (as Admin) removes Peer A from the group via the Main Node UI *(or via the Simulated Peer UI if they are the admin)*.
2. Admin queues a standard `MESSAGE` payload to Relay R containing a `GroupAdminCommand` to remove Peer A's routing token.
3. User clicks **Deliver** on the payload in Relay R's queue. Relay R drops the token.
4. Admin rotates the `GroupMasterKey` and increments the Group Epoch.
5. Admin queues standard 1:1 `MESSAGE` payloads containing the new `GroupMasterKey` to all remaining active peers (via Relay R).
6. User clicks **Next** / **Deliver** on the payloads into Relay R, and then **Deliver** on the fanned-out payloads to the remaining active peers.
7. Simulator **Sessions** tab reflects the incremented **Group Epoch** for all active participants.

### Flow 14 — Group Leave Operation
**Goal:** Validate voluntary departure from a group and subsequent routing updates.
1. Any participant (Main Node or Simulated Peer) initiates a "Leave Group" action.
2. Participant queues a `LeaveGroupCommand` payload routed to Relay R.
3. User clicks **Deliver** on the payload in Relay R's queue.
4. Relay R drops the departing peer's blinded routing token from its table.
5. Departing peer clears its local group state.
6. Group Admin detects the departure and executes a Group Master Key rotation (following Flow 13, Steps 4-7) to enforce forward secrecy.

### Flow 15 — State Persistence & Reset
**Goal:** Ensure continuity across development sessions.
1. Simulator state (Peers, Queues, Ratchet Indexes, Blinded Routing Tables) is persisted to local storage.
2. Upon browser refresh/app restart, the simulator reloads the exact state. Active Double Ratchets and Group Epochs remain perfectly in sync with the Main Node.
3. User can explicitly trigger a **Reset State** to wipe simulator storage without affecting Main Node production data.
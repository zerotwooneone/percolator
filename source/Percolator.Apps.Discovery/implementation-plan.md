# Percolator.Apps.Discovery Implementation Plan

## 1. Executive Summary & Core Architectural Goals

The **Discovery Plugin** (`Percolator.Apps.Discovery`, `AppId = 0x02`) provides privacy-preserving peer rendezvous and relay/peer-mediated node discovery.

The fundamental operation of discovery in Percolator is **asking a chosen peer (whether a dedicated relay or an individual node) for "nearby" discoverable peers**.

Following the **asynchronous, store-and-forward design of the Signal Protocol**, the plugin:
* Does **not** track volatile online/offline status or require connection heartbeats.
* Operates strictly on **manual, user-driven intent** (zero background network polling).
* Enforces **two-phase discovery** so passive directory queries never consume One-Time Prekeys (OPKs).
* Unifies the end-to-end user workflow into cohesive **vertical feature slices** rather than grouping code by technical layer.
* Protects against primitive obsession and operator conversions by enforcing self-validating Value Objects.
* Adheres strictly to **DDD Onion Architecture** depending only on `Percolator.Domain` and `Percolator.PluginSdk`.

---

## 2. Security Boundaries & Protocol Invariants

### 2.1 Invocation Model: Manual-Only
* **Zero Ambient Polling**: Discovery queries are triggered exclusively by explicit user intent (e.g. asking a connected relay or peer for nearby nodes).
* Eliminates background traffic correlation, battery drain, and radio fingerprinting.

### 2.2 Two-Phase Discovery (OPK Depletion Protection)
* **The Cryptographic Dilemma**: An X3DH `PreKeyBundle` contains ephemeral One-Time PreKeys (OPKs). Returning and consuming OPKs during a casual discovery scan allows an adversary to sweep a relay and exhaust every user's OPKs without ever initiating a conversation.
* **Separation of Responsibilities**:
  * **Step 1 (Discovery Query)**: Asking a target peer/relay returns lightweight `PeerDiscoveryDescriptor` records (`PublicIdentityId`, `DeliveryRoute`). **Zero One-Time Prekeys are popped or burned.**
  * **Step 2 (Handshake on Intent to Connect)**: When establishing a session with a discovered candidate, the application calls the prekey provider to pop a fresh OPK and initializes an X3DH session via `IHandshakeService`.
  * **Workflow Orchestration**: The `NearbyPeerDiscovery` slice coordinates Step 1 and Step 2 into a complete flow, while exposing pure Step 1 and Step 2 individually.

### 2.3 Hard Admission Gate: Handshake + Active Signed PreKey (SPK)
* **PreKey Requirement (Signal Parity)**: A peer is eligible for discovery admission if and only if they have:
  1. An authenticated cryptographic handshake with the hosting peer/relay.
  2. A valid, unexpired **Signed PreKey (SPK)** with a verified signature.
* **One-Time Prekeys are strictly optional**: In accordance with X3DH, depletion of ephemeral OPKs does **not** disqualify a peer from discovery (the protocol seamlessly falls back to Triple-DH until new OPKs are uploaded).

### 2.4 Sealed Sender Throttling & Sane Result Bounds
* Ingress traffic is naturally throttled by Percolator's existing sealed sender token issuance.
* To prevent excessive memory allocation or unbounded response packets, discovery queries enforce a strongly typed `DiscoveryResultLimit` (defaulting to 50, bounded between 1 and 100).

### 2.5 Epoch-Windowed Blinded Locators
* To prevent semi-honest relays or eavesdroppers from correlating user presence over time, blinded locators are ratcheted by day/epoch:
  $$\text{Locator} = \text{HMAC-SHA256}(\text{ContactSharedSecret}, \text{EpochDay} \parallel \text{PublicIdentityId})$$
* Encapsulated in the `BlindedLocator` value object.

### 2.6 Cryptographic Registration Authority
* A peer can **only** submit discovery visibility registrations (`Discoverable` vs `Hidden`) for its own `PublicIdentityId`.
* The hosting peer/relay enforces that the registration identity strictly matches the authenticated session identity established during the transport handshake.

### 2.7 Route Privacy
* Discovery descriptors return relay-mediated route enum values (`DeliveryRoute.RelayedOneToOne`), concealing the remote peer's actual IP address and physical location.

### 2.8 Elimination of Primitive Obsession & Operator Conversions
* No bare strings or unbounded ints for domain concepts with invariants.
* Every value object enforces validation in static factory methods (`Create()`, `New()`) and exposes values via `.Value` and `ToString()`.
* **Zero `implicit operator` or `explicit operator` conversions.**

---

## 3. Vertical Feature Slices

The codebase is organized into vertical feature slices under `Percolator.Apps.Discovery`:

```
Percolator.Apps.Discovery/
├── DiscoveryPlugin.cs                          # IAppPlugin (AppId = 0x02)
│
├── Features/
│   ├── NearbyPeerDiscovery/                   # Feature Slice 1: Client query & handshake workflow
│   │   ├── DiscoveryResultLimit.cs            # Value Object: 1 <= limit <= 100
│   │   ├── PeerDiscoveryDescriptor.cs         # Read Model: PublicIdentityId, DeliveryRoute
│   │   ├── DiscoveredPeerConnectionResult.cs  # Result Model: Connected sessions & granular errors
│   │   ├── NearbyDiscoveryQueryPayload.cs     # Inbound query DTO
│   │   ├── NearbyDiscoveryResponsePayload.cs  # Outbound response DTO
│   │   ├── INearbyPeerDiscoveryWorkflow.cs    # Contract: Step 1, Step 2, and composite workflow
│   │   └── NearbyPeerDiscoveryWorkflow.cs     # Coordinates Query -> PreKey Fetch -> Handshake
│   │
│   ├── PrivateRendezvous/                     # Feature Slice 2: Blinded mutual-contact discovery
│   │   ├── DiscoveryEpoch.cs                  # Value Object: Day/epoch time window
│   │   ├── BlindedLocator.cs                  # Value Object: HMAC token
│   │   ├── BlindedRendezvousQueryPayload.cs   # Wire query DTO
│   │   ├── BlindedRendezvousResponsePayload.cs# Wire response DTO
│   │   ├── IPrivateRendezvousService.cs       # Contract: Derives locators & queries rendezvous
│   │   └── PrivateRendezvousService.cs        # Implementation
│   │
│   └── RelayDirectoryHosting/                 # Feature Slice 3: Hosting discoverability & admission
│       ├── RelayDiscoveryPolicy.cs            # Policy Enum: Disabled, Open, BlindedContactsOnly
│       ├── PeerDiscoveryVisibility.cs         # Visibility Enum: Hidden (default), Discoverable
│       ├── IDiscoveryAdmissionGate.cs         # Admission contract
│       ├── DiscoveryAdmissionGate.cs          # Enforces handshake + active SPK verification
│       ├── DiscoveryRegistrationPayload.cs     # Inbound peer visibility registration DTO
│       ├── IRelayPeerDirectory.cs             # Directory storage port
│       └── InMemoryRelayPeerDirectory.cs      # In-memory directory implementation
│
├── Ingress/
│   └── DiscoveryPayloadHandler.cs             # IAppPayloadHandler: Dispatches inbound wire payloads
│
└── Ports/
    ├── IPreKeyAvailabilityPort.cs             # Port: Checks peer SPK freshness without loading keys
    └── IPreKeyBundleProvider.cs               # Port: Fetches consumable PreKeyBundle on connect
```

---

## 4. Implementation Milestones

### Milestone 1: Domain Primitives & Hosting Slice (`RelayDirectoryHosting`)
* **Domain Primitives**:
  * `RelayDiscoveryPolicy` enum (`Disabled`, `Open`, `BlindedContactsOnly`).
  * `PeerDiscoveryVisibility` enum (`Hidden`, `Discoverable`).
* **Admission Gate**:
  * `IDiscoveryAdmissionGate` & `DiscoveryAdmissionGate`: validates relay policy, peer visibility, authenticated handshake state, and unexpired Signed PreKey ($SPK$) presence.
* **Directory Storage & Registration**:
  * `IRelayPeerDirectory` & `InMemoryRelayPeerDirectory`.
  * `DiscoveryRegistrationPayload` handling with session-authentication verification.
* **Unit Tests** (`Percolator.Apps.Discovery.Tests/RelayDirectoryHosting`):
  * State-based admission gate tests covering all policy/visibility matrices.
  * Directory registration, visibility toggling, and query filtering.

### Milestone 2: Private Rendezvous Feature Slice (`PrivateRendezvous`)
* **`DiscoveryEpoch`**:
  * Value object encapsulating `long EpochDays`. Factory `DiscoveryEpoch.FromUtc(DateTimeOffset)`.
* **`BlindedLocator`**:
  * Value object encapsulating 32-byte cryptographic token.
  * Factory `BlindedLocator.Derive(PublicIdentityId peerId, ReadOnlySpan<byte> sharedSecret, DiscoveryEpoch epoch)`.
  * Validates non-empty, strict 32-byte span. Exposes `.Value` and `ToString()`. Zero operators.
* **`IPrivateRendezvousService` & `PrivateRendezvousService`**:
  * Derives active and next-epoch locators for mutual contacts.
  * Queries and resolves rendezvous points.
* **Unit Tests** (`Percolator.Apps.Discovery.Tests/PrivateRendezvous`):
  * Deterministic HMAC derivation and epoch window rotation using virtualized time.
  * Blinded locator query resolution.

### Milestone 3: Nearby Peer Discovery Feature Slice (`NearbyPeerDiscovery`)
* **Primitives & DTOs**:
  * `DiscoveryResultLimit` value object ($1 \le \text{limit} \le 100$).
  * `PeerDiscoveryDescriptor` record (`PublicIdentityId`, `DeliveryRoute`).
  * `NearbyDiscoveryQueryPayload` & `NearbyDiscoveryResponsePayload`.
* **Two-Step Workflow**:
  * `INearbyPeerDiscoveryWorkflow` & `NearbyPeerDiscoveryWorkflow`:
    * Step 1: Query target peer/relay for candidate descriptors.
    * Step 2: Fetch consumable prekey bundle and initiate X3DH handshake via `IHandshakeService`.
    * Composite: `DiscoverAndConnectAsync` executing Step 1 then Step 2 with granular error tracking (`DiscoveredPeerConnectionResult`).
* **Unit Tests** (`Percolator.Apps.Discovery.Tests/NearbyPeerDiscovery`):
  * Query parsing, result clamping, and descriptor construction.
  * Sequential orchestration: Step 1 (query) followed by Step 2 (handshake).
  * Fault tolerance: failure on one candidate does not abort handshakes with other candidates.
  * Verified OPK protection: prekeys are never consumed during Step 1.

### Milestone 4: Ingress Handler & Plugin Assembly (`Ingress`)
* **`DiscoveryPayloadHandler`**:
  * Implements `IAppPayloadHandler` for `AppId.Discovery` (`0x02`).
  * Routes inbound wire payloads to `RelayDirectoryHosting`, `PrivateRendezvous`, or `NearbyPeerDiscovery`.
  * Enforces session authentication: rejects registration payloads where the payload ID does not match the authenticated session ID.
* **`DiscoveryPlugin`**:
  * Implements `IAppPlugin` declaring capabilities and dependencies.
* **Unit Tests** (`Percolator.Apps.Discovery.Tests/Ingress`):
  * Inbound payload routing and dispatching.
  * Session-mismatch impersonation rejection.

---

## 5. Verification & Testing Strategy (High-Value, Low-Brittle Tests)

To prevent brittle tests that break during internal refactorings while guaranteeing mission-critical cryptographic and privacy properties, testing adheres strictly to the following standards:

### 5.1 Test Design Rules
1. **State-Based & Behavioral Verification (No Mock Call Counting)**:
   * Tests assert on returned domain values (`DomainResult`, descriptors, active ratchet sessions, error codes).
   * Avoid `mock.Verify(x => x.SomeInternalCall(), Times.Once)`; verify outcomes and observable state transitions instead.
2. **In-Memory Doubles Over Fragile Mocks**:
   * Use stateful in-memory implementations (`InMemoryRelayPeerDirectory`, `FakePreKeyBundleProvider`, `FakeHandshakeService`) that behave like real dependencies.
3. **Deterministic Virtual Time**:
   * All time-sensitive calculations (`DiscoveryEpoch`, SPK freshness) inject `IDateTimeProvider` (`FakeDateTimeProvider`), eliminating flaky timer or wall-clock dependencies.
4. **Zero Ritualistic Boilerplate**:
   * Tests must test domain logic and protocol invariants. Avoid redundant tests for trivial compiler-generated getters/setters.

### 5.2 Key Invariant Test Matrix

| Test Category | Target Invariant Under Test | Observable Verification |
| :--- | :--- | :--- |
| **Relay Admission Matrix** | `RelayDiscoveryPolicy.Disabled` rejects all queries | `DiscoverPeersAsync` returns empty list or failure with `RELAY_DISCOVERY_DISABLED`. |
| **Peer Stealth Mode** | `PeerDiscoveryVisibility.Hidden` excludes peer from results | Hidden peer is never listed in discovery responses even when `Policy == Open`. |
| **Uncontactable Suppression** | Missing handshake or expired SPK excludes peer | Candidate without active SPK is omitted from directory responses. |
| **Anti-Impersonation** | Node A cannot alter Node B's visibility | Ingress returns `DomainResult.Failure("UNAUTHORIZED_REGISTRATION")` when `payload.PeerId != context.SenderIdentityId`. |
| **Epoch Isolation** | Day boundary ratchets blinded locators | `BlindedLocator.Derive` on Day $N$ differs from Day $N+1$ for the same peer and secret. |
| **Mutual Determinism** | Both peers derive identical blinded locators | Alice and Bob derive matching locators given their pairwise shared secret. |
| **OPK Starvation Protection** | Step 1 query never consumes prekeys | Step 1 (`QueryNearbyPeersAsync`) leaves `AvailableOneTimePreKeysCount` unchanged on the prekey store. |
| **Workflow Fault Tolerance** | Handshake error on peer 1 does not abort peer 2 | `DiscoverAndConnectAsync` returns 1 successful session in `ConnectedPeers` and 1 failure in `Failures`. |
| **Result Clamping** | Over-sized limit requests are clamped | Requesting limit of 500 returns at most 100 descriptors. |

---

## 6. Execution Quality Checklist
* All tests pass cleanly (`dotnet test Percolator.sln`).
* Zero `implicit operator` or `explicit operator` conversions.
* Zero infrastructure dependencies in `Percolator.Apps.Discovery` (pure domain/application contracts).

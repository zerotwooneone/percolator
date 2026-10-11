# Percolator.Apps.Discovery Implementation Plan

## 1. Executive Summary & Core Architectural Goals

The **Discovery Plugin** (`Percolator.Apps.Discovery`, `AppId = 0x02`) provides privacy-preserving peer rendezvous and relay-mediated node discovery.

Following the **asynchronous, store-and-forward design of the Signal Protocol**, the plugin:
* Does **not** track volatile online/offline status or require connection heartbeats.
* Operates strictly on **manual, user-driven intent** (zero background network polling).
* Enforces **two-phase discovery** so passive directory scans never consume One-Time Prekeys (OPKs).
* Protects against primitive obsession and operator conversions by enforcing self-validating Value Objects.
* Adheres strictly to **DDD Onion Architecture** organized in **vertical feature slices**.

---

## 2. Security Boundaries & Protocol Invariants

### 2.1 Invocation Model: Manual-Only
* **Zero Ambient Polling**: Discovery queries are triggered exclusively by explicit user intent (e.g. clicking *"Scan Local Network"* or *"Discover Relay Peers"*).
* Eliminates background traffic correlation, battery drain, and radio fingerprinting.

### 2.2 Two-Phase Discovery (OPK Depletion Protection)
* **Phase 1 (Discovery Query)**: Returns lightweight `PeerDiscoveryDescriptor` records containing `PublicIdentityId` and `DeliveryRoute`. **Zero One-Time Prekeys are popped or burned.**
* **Phase 2 (Handshake on Intent to Chat)**: When the user chooses to start a conversation, the client fetches the fresh prekey bundle via the dedicated prekey endpoint and executes X3DH.

### 2.3 Hard Admission Gate: Handshake + Active Signed PreKey (SPK)
* **PreKey Requirement (Signal Parity)**: A peer is eligible for discovery admission if and only if they have:
  1. An authenticated cryptographic handshake with the relay.
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
* The relay enforces that the registration identity strictly matches the authenticated session identity established during the transport handshake.

### 2.7 Route Privacy
* **LAN Discovery**: Descriptors return direct local routes (`DeliveryRoute.Direct(...)`).
* **WAN Relay Discovery**: Descriptors return relay-mediated routes (`DeliveryRoute.ViaRelay(...)`), concealing the peer's actual IP address and physical location.

### 2.8 Elimination of Primitive Obsession & Operator Conversions
* No bare strings or unbounded ints for domain concepts with invariants.
* Every value object enforces validation in static factory methods (`Create()`, `New()`) and exposes values via `.Value` and `ToString()`.
* **Zero `implicit operator` or `explicit operator` conversions.**

---

## 3. Vertical Feature Slices

The codebase is organized into vertical feature slices under `Percolator.Apps.Discovery`:

```
Percolator.Apps.Discovery/
├── DiscoveryPlugin.cs                      # IAppPlugin (AppId = 0x02)
├── Features/
│   ├── RelayPolicy/                       # Feature Slice: Relay sovereignty & policies
│   │   ├── RelayDiscoveryPolicy.cs        # Enum: Disabled, OpenLan, BlindedContactsOnly
│   │   ├── RelayDiscoveryConfig.cs        # Value Object: Policy + MaxResults + per-peer blocklist
│   │   └── IRelayPolicyEvaluator.cs       # Evaluates if query scope is permitted
│   │
│   ├── Admission/                         # Feature Slice: Pre-condition gatekeeping
│   │   ├── IDiscoveryAdmissionGate.cs     # Admission contract
│   │   └── DiscoveryAdmissionGate.cs      # Enforces handshake + active SPK verification
│   │
│   ├── PeerVisibility/                    # Feature Slice: Sovereign peer visibility ("Stealth")
│   │   ├── PeerDiscoveryVisibility.cs     # Enum: Hidden (default), Discoverable
│   │   ├── DiscoveryRegistrationDto.cs    # Registration payload
│   │   └── PeerVisibilityRegistry.cs      # Session-authenticated visibility state
│   │
│   ├── BlindedLocators/                   # Feature Slice: Epoch-windowed contact locators
│   │   ├── DiscoveryEpoch.cs              # Value Object: Day/epoch time window
│   │   ├── BlindedLocator.cs              # Value Object: HMAC-derived locator token
│   │   └── IBlindedLocatorService.cs      # Derives active & next-epoch locators
│   │
│   └── Directory/                         # Feature Slice: Discovery query & CQRS read models
│       ├── DiscoveryResultLimit.cs        # Value Object: Validates 1 <= limit <= 100
│       ├── PeerDiscoveryDescriptor.cs     # Record: PublicIdentityId, DeliveryRoute
│       ├── DiscoveryQueryPayload.cs       # Inbound query DTO
│       ├── DiscoveryResponsePayload.cs    # Outbound response DTO
│       ├── IPeerDiscoveryDirectory.cs     # Read/Write directory port
│       └── IDiscoveryQueryService.cs      # CQRS Read query port
│
├── Ingress/
│   └── DiscoveryPayloadHandler.cs         # IAppPayloadHandler: Routes payloads to slices
│
└── Ports/
    └── IPreKeyAvailabilityPort.cs         # Port to check peer SPK freshness without loading keys
```

---

## 4. Implementation Milestones

### Milestone 1: Domain Primitives & Feature Slices (`RelayPolicy`, `Admission`, `PeerVisibility`, `BlindedLocators`)
* **`DiscoveryEpoch`**:
  * Value object encapsulating `long EpochDays`. Factory `DiscoveryEpoch.FromUtc(DateTimeOffset)`.
* **`BlindedLocator`**:
  * Value object encapsulating 32-byte cryptographic token.
  * Factory `BlindedLocator.Derive(PublicIdentityId peerId, ReadOnlySpan<byte> sharedSecret, DiscoveryEpoch epoch)`.
  * Validates non-empty, strict 32-byte span. Exposes `.Value` and `ToString()`. Zero operators.
* **`RelayDiscoveryPolicy` & `PeerDiscoveryVisibility`**:
  * Strict enums with safe defaults (`RelayDiscoveryPolicy.Disabled`, `PeerDiscoveryVisibility.Hidden`).
* **`IDiscoveryAdmissionGate` & `DiscoveryAdmissionGate`**:
  * Evaluates relay policy, peer visibility, handshake state, and unexpired Signed PreKey ($SPK$) presence.
* **Unit Tests** (`Percolator.Apps.Discovery.Tests`):
  * Blinded locator deterministic derivation and epoch isolation tests.
  * Admission gate assertions across all policy combinations.

### Milestone 2: Directory Feature Slice & CQRS Read Port (`Directory`)
* **`DiscoveryResultLimit`**:
  * Value object validating limit between 1 and 100 (defaults to 50).
* **`PeerDiscoveryDescriptor`**:
  * Carries `PublicIdentityId` and route-masked `DeliveryRoute`.
* **`IPeerDiscoveryDirectory` & `InMemoryPeerDiscoveryDirectory`**:
  * In-memory store for relay-registered peers and their SPK availability.
* **`IDiscoveryQueryService`**:
  * `Task<IReadOnlyList<PeerDiscoveryDescriptor>> DiscoverPeersAsync(DiscoveryScope scope, DiscoveryResultLimit limit, CancellationToken ct);`
  * `Task<PeerDiscoveryDescriptor?> FindByBlindedLocatorAsync(BlindedLocator locator, CancellationToken ct);`
* **Unit Tests**:
  * Directory query filtering, result bounds clamping, and blinded locator resolution.

### Milestone 3: Ingress Handler & Plugin Registration (`Ingress`)
* **`DiscoveryQueryPayload` & `DiscoveryResponsePayload`**:
  * Pure application DTOs (Protobuf serialization abstracted via `IPayloadSerializer`).
* **`DiscoveryPayloadHandler`**:
  * Implements `IAppPayloadHandler` for `AppId.Discovery` (`0x02`).
  * Enforces session authentication: rejects registration payloads where the payload ID does not match the authenticated session ID.
* **`DiscoveryPlugin`**:
  * Implements `IAppPlugin` declaring capabilities and dependencies.
* **Unit Tests**:
  * Ingress dispatching, impersonation rejection, and payload routing.

---

## 5. Verification & Testing Strategy
* 100% unit test coverage in `Percolator.Apps.Discovery.Tests` using in-memory test doubles.
* Virtualized time using `FakeDateTimeProvider` for epoch computations.
* Full solution test validation: run `dotnet test Percolator.sln` to guarantee zero regressions across all projects.

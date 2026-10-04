# Percolator.Apps.Discovery Implementation Plan

## Summary & Architectural Constraints
- **Target Project**: `Percolator.Apps.Discovery` (Decentralized peer discovery, Kademlia DHT rendezvous, and blinded contact locator queries).
- **Architectural Rules (Rule 1 & Rule 2)**:
  - Depends **only** on `Percolator.Domain` and `Percolator.PluginSdk`.
  - Zero reference to infrastructure/transport/storage libraries (no UDP sockets, raw TCP, SQLite, or OS APIs).
  - Strict serialization boundary: Application layer handles pure C# DTOs and delegates serialization to `IPayloadSerializer`. Concrete Protobuf contracts (`.proto`) and Google Protobuf code live strictly in `Percolator.Infrastructure2.Serialization`.
  - Implements `IAppPlugin` (`AppId.Discovery = 0x02`) and `IAppPayloadHandler` from `Percolator.PluginSdk`.
  - **CQRS Separation (Rendezvous State vs. Fast Read Queries)**:
    - **Write Path**: State updates (rendezvous announcements, contact locator registrations, DHT bucket routing) update internal models or domain repositories.
    - **Read Path**: The UI and routing layers query peer reachability, presence summaries, and locator status via a dedicated read-only query port (`IDiscoveryQueryService`), skipping domain aggregate loading or full DHT bucket traversal.
  - **Prerequisite Handshake & Prekey Bundle Invariant**:
    - Discovery announcement and locator queries **require** that the remote peer has already successfully completed a mutual cryptographic handshake AND has an active, valid `PreKeyBundle` available.
    - Peered nodes without established trust or unconsumable pre-keys are suppressed from rendezvous announcements and reachability resolution. This ensures nodes can never be advertised if communication cannot be initiated, and grants peers sovereign control over whether and how they can be discovered.
  - **Cryptographic Logging Guardrails**:
    - The Discovery application must strictly honor cryptographic logging guardrails (`CryptographyOptions.EnableCryptographicMaterialLogging = false` by default).
    - Diagnostic and trace logging must **never** leak contact shared secrets, blinded locator pre-image secrets, node private keys, or pre-key cryptographic components. Only non-sensitive metrics (e.g. bucket IDs, ping round-trip times, candidate counts) may be emitted.
  - Test-first implementation: All behaviors must have corresponding unit tests in `Percolator.Apps.Discovery.Tests` using in-memory test doubles.

---

## Milestone 1: Discovery Plugin & Payload Architecture

### 1.1 Plugin Definition & Discovery DTOs
- **`DiscoveryPlugin`**: Implements `IAppPlugin` with `AppId = 0x02` and semantic versioning.
- **Application Payload DTOs** (Protobuf serialization abstracted via `IPayloadSerializer`):
  - `DhtPingPayload`: Node ID, sequence number, and network epoch.
  - `DhtPongPayload`: Acknowledgment, responder node descriptor, candidate relay locators, and active signed pre-key reference.
  - `DhtFindNodePayload`: Target ID lookup request.
  - `DhtNodeListPayload`: Nearest $k$ contact descriptors (filtered by handshake and pre-key validity).
- **`DiscoveryPayloadHandler`**:
  - Implements `IAppPayloadHandler` for `AppId.Discovery`.
  - Routes inbound ping/find-node payloads to the rendezvous and routing engine.
  - Rejects/ignores discovery announcements from unknown peers that do not provide consumable pre-key bundles.

### 1.2 Test Doubles & Unit Tests (`Percolator.Apps.Discovery.Tests/Ingress`)
- `DiscoveryPayloadHandlerTests.HandleInboundAsync_PingWithValidPreKey_ReturnsPongWithRelayDescriptor`: asserts rendezvous ping/pong response.
- `DiscoveryPayloadHandlerTests.HandleInboundAsync_PingWithoutPreKeyBundle_RejectsAnnouncement`: asserts uncontactable nodes are dropped.
- `DiscoveryPayloadHandlerTests.HandleInboundAsync_FindNode_ReturnsNearestCandidateNodes`: asserts node lookup dispatching.

---

## Milestone 2: Blinded Contact Locators

### 2.1 Privacy-Preserving Locator Derivation & Handshake Gating
- **`BlindedLocator` Value Object**:
  - Derives deterministic blinded query token: `SHA256(PublicIdentityId || ContactSharedSecret)`.
  - Enables mutual contacts to locate each other on relays or DHT nodes without exposing public identity keys to intermediaries or passive eavesdroppers.
- **`IBlindedLocatorService` & `BlindedLocatorService`**:
  - Validates that target peer has an active handshake and valid pre-key bundle before computing or publishing locators.
  - Computes active and next-epoch query locators for approved `PeerContact` instances.
  - Generates lookup requests to discovery relays.

### 2.2 Test Doubles & Unit Tests (`Percolator.Apps.Discovery.Tests/Locators`)
- `BlindedLocatorTests.ComputeLocator_IsDeterministicAndMatchesSharedSecret`: verifies zero linkability for non-contacts.
- `BlindedLocatorTests.ComputeLocator_DifferentSecret_ProducesUncorrelatedHash`: verifies cryptographic privacy.
- `BlindedLocatorTests.PublishLocator_WhenHandshakeMissing_SuppressesPublication`: asserts handshake requirement invariant.

---

## Milestone 3: Rendezvous, Presence State Machine & Fast Queries

### 3.1 Presence Ticket Management
- **`RendezvousTicket` Value Object/Entity**:
  - Tracks peer presence tickets (`PeerId`, `BlindedLocator`, `RelayEndpointDescriptor`, `RegisteredAtUtc`, `ExpiresAtUtc`, `HasPreKeyBundle`).
- **`RendezvousStateMachine`**:
  - Registers active routing descriptors for local identities.
  - Enforces that presence tickets are only granted and announced when valid pre-key bundles and handshakes are established.
  - Prunes expired presence tickets using domain clock abstractions (`IDateTimeProvider`).
  - Answers contact reachability inquiries queried by `IRoutingCoordinator` (`IPeerReachabilityService`).

### 3.2 Read-Only Discovery Query Port
- **`IDiscoveryQueryService`** (`Percolator.Apps.Discovery.Ports`):
  - Read-only query port for UI presence dashboards and fast contact reachability queries:
    - `Task<IReadOnlyList<PeerPresenceReadModel>> GetActivePeerPresencesAsync(CancellationToken ct = default);`
    - `Task<PeerPresenceReadModel?> GetPeerPresenceAsync(PublicIdentityId peerId, CancellationToken ct = default);`
  - Bypasses DHT bucket rebalancing or domain aggregate locking, querying the localized routing cache directly.

### 3.3 Test Doubles & Unit Tests (`Percolator.Apps.Discovery.Tests/Rendezvous`)
- `RendezvousStateMachineTests.Register_WhenTtlExpired_PurgesExpiredTickets`: asserts ticket expiration pruning with virtual time.
- `RendezvousStateMachineTests.ResolveTicket_WhenValid_ReturnsEndpointDescriptor`: asserts active ticket retrieval.
- `RendezvousStateMachineTests.ResolveTicket_WhenPeerHasNoPreKeyBundle_ReturnsUnreachable`: asserts pre-key requirement.
- `DiscoveryQueryServiceTests.GetActivePeerPresencesAsync_ReturnsActivePresences`: asserts fast read query accuracy.

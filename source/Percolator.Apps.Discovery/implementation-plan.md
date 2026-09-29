# Percolator.Apps.Discovery Implementation Plan

## Summary & Architectural Constraints
- **Target Project**: `Percolator.Apps.Discovery` (Decentralized peer discovery, Kademlia DHT rendezvous, and blinded contact locator queries).
- **Architectural Rules (Rule 1 & Rule 2)**:
  - Depends **only** on `Percolator.Domain` and `Percolator.PluginSdk`.
  - Zero reference to infrastructure/transport/storage libraries (no UDP sockets, raw TCP, SQLite, or OS APIs).
  - Strict serialization boundary: Application layer handles pure C# DTOs and delegates serialization to `IPayloadSerializer`. Concrete Protobuf contracts (`.proto`) and Google Protobuf code live strictly in `Percolator.Infrastructure2.Serialization`.
  - Implements `IAppPlugin` (`AppId.Discovery = 0x02`) and `IAppPayloadHandler` from `Percolator.PluginSdk`.
  - Test-first implementation: All behaviors must have corresponding unit tests in `Percolator.Apps.Discovery.Tests` using in-memory test doubles.

---

## Milestone 1: Discovery Plugin & Payload Architecture

### 1.1 Plugin Definition & Discovery DTOs
- **`DiscoveryPlugin`**: Implements `IAppPlugin` with `AppId = 0x02` and semantic versioning.
- **Application Payload DTOs** (Protobuf serialization abstracted via `IPayloadSerializer`):
  - `DhtPingPayload`: Node ID, sequence number, and network epoch.
  - `DhtPongPayload`: Acknowledgment, responder node descriptor, and candidate relay locators.
  - `DhtFindNodePayload`: Target ID lookup request.
  - `DhtNodeListPayload`: Nearest $k$ contact descriptors.
- **`DiscoveryPayloadHandler`**:
  - Implements `IAppPayloadHandler` for `AppId.Discovery`.
  - Routes inbound ping/find-node payloads to the rendezvous and routing engine.

### 1.2 Test Doubles & Unit Tests (`Percolator.Apps.Discovery.Tests/Ingress`)
- `DiscoveryPayloadHandlerTests.HandleInboundAsync_Ping_ReturnsPongWithRelayDescriptor`: asserts rendezvous ping/pong response.
- `DiscoveryPayloadHandlerTests.HandleInboundAsync_FindNode_ReturnsNearestCandidateNodes`: asserts node lookup dispatching.

---

## Milestone 2: Blinded Contact Locators

### 2.1 Privacy-Preserving Locator Derivation
- **`BlindedLocator` Value Object**:
  - Derives deterministic blinded query token: `SHA256(PublicIdentityId || ContactSharedSecret)`.
  - Enables mutual contacts to locate each other on relays or DHT nodes without exposing public identity keys to intermediaries or passive eavesdroppers.
- **`IBlindedLocatorService` & `BlindedLocatorService`**:
  - Computes active and next-epoch query locators for approved `PeerContact` instances.
  - Generates lookup requests to discovery relays.

### 2.2 Test Doubles & Unit Tests (`Percolator.Apps.Discovery.Tests/Locators`)
- `BlindedLocatorTests.ComputeLocator_IsDeterministicAndMatchesSharedSecret`: verifies zero linkability for non-contacts.
- `BlindedLocatorTests.ComputeLocator_DifferentSecret_ProducesUncorrelatedHash`: verifies cryptographic privacy.

---

## Milestone 3: Rendezvous & Presence State Machine

### 3.1 Presence Ticket Management
- **`RendezvousTicket` Value Object/Entity**:
  - Tracks peer presence tickets (`PeerId`, `BlindedLocator`, `RelayEndpointDescriptor`, `RegisteredAtUtc`, `ExpiresAtUtc`).
- **`RendezvousStateMachine`**:
  - Registers active routing descriptors for local identities.
  - Prunes expired presence tickets using domain clock abstractions (`IDateTimeProvider`).
  - Answers contact reachability inquiries queried by `IRoutingCoordinator`.

### 3.2 Test Doubles & Unit Tests (`Percolator.Apps.Discovery.Tests/Rendezvous`)
- `RendezvousStateMachineTests.Register_WhenTtlExpired_PurgesExpiredTickets`: asserts ticket expiration pruning with virtual time.
- `RendezvousStateMachineTests.ResolveTicket_WhenValid_ReturnsEndpointDescriptor`: asserts active ticket retrieval.

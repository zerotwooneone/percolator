# Gold Standard Plan Example: Sovereign Peer Discovery

This document serves as the canonical reference implementation for an architectural implementation plan refined across all 5 lenses.

---

# Percolator.Apps.Discovery Implementation Plan

## Refinement Status Gate
- [x] Lens 1: Scope & Feature Slice Framing (Status: Approved)
- [x] Lens 2: Onion Architecture & Primitive Obsession (Status: Approved)
- [x] Lens 3: Threat Modeling & Security Posture (Status: Approved)
- [x] Lens 4: High-Value, Low-Brittle Unit Test Design (Status: Approved)
- [x] Lens 5: Cross-Layer Infrastructure Alignment (Status: Approved)
- [x] Implementation Gate: UNLOCKED (Approved for Code Generation)

---

## 1. Scope & Architectural Boundaries

### 1.1 In-Scope Capabilities
- **Nearby Peer Discovery via Relays/Peers**: Query candidate peer descriptors from a chosen sovereign relay or peer node without consuming cryptographic prekeys.
- **Private Blinded Rendezvous**: Deterministic zero-knowledge contact matching across relays using daily rotating HMAC-SHA256 handles.
- **Sovereign Directory Hosting**: Relay-side peer registration store enforcing admission gates and query clamping.
- **Two-Phase Discovery Orchestration**: Workflow orchestrating Step 1 (candidate query) and Step 2 (on-demand prekey bundle retrieval and X3DH handshake).

### 1.2 Explicit Non-Goals
- **No Direct Socket Transport**: Sockets, TLS handshakes, and gRPC streaming are delegated exclusively to `Percolator.Infrastructure`.
- **No Continuous Presence / Heartbeats**: Nodes function strictly as store-and-forward actors (Signal Protocol parity); zero online/offline tracking.
- **No Global DHT or LAN Multicast**: Decentralized Kademlia routing tables and unauthenticated UDP LAN broadcasts are excluded to prevent metadata leakage.

---

## 2. Vertical Feature Slice Architecture

```
Percolator.Apps.Discovery/
├── Features/
│   ├── NearbyPeerDiscovery/
│   │   ├── DiscoveryResultLimit.cs         # Value Object: 1 <= limit <= 100
│   │   ├── PeerDiscoveryDescriptor.cs      # Candidate view model
│   │   ├── DiscoveredPeerConnectionResult.cs
│   │   ├── NearbyDiscoveryPayloads.cs      # Query/Response wire payloads
│   │   ├── INearbyPeerDiscoveryWorkflow.cs
│   │   └── NearbyPeerDiscoveryWorkflow.cs  # Two-phase orchestrator
│   ├── RelayDirectoryHosting/
│   │   ├── RelayDiscoveryPolicy.cs         # Disabled | Open | BlindedContactsOnly
│   │   ├── PeerDiscoveryVisibility.cs      # Hidden | Discoverable
│   │   ├── IDiscoveryAdmissionGate.cs      # SPK + Handshake verification
│   │   ├── DiscoveryAdmissionGate.cs
│   │   ├── RelayPeerRegistration.cs
│   │   ├── IRelayPeerDirectory.cs          # Directory storage interface
│   │   ├── InMemoryRelayPeerDirectory.cs
│   │   └── DiscoveryRegistrationPayload.cs
│   └── PrivateRendezvous/
│       ├── DiscoveryEpoch.cs               # Value Object: 24-hr UTC day window
│       ├── BlindedLocator.cs               # Value Object: 32-byte HMAC-SHA256
│       ├── BlindedRendezvousPayloads.cs
│       ├── IPrivateRendezvousService.cs
│       └── PrivateRendezvousService.cs
├── Ports/
│   ├── IPreKeyAvailabilityPort.cs          # Admission gate prekey verification
│   ├── IPreKeyBundleProvider.cs            # Step 2 on-demand bundle retrieval
│   └── IDiscoveryClientPorts.cs            # Handshake & relay query RPC ports
├── Ingress/
│   └── DiscoveryPayloadHandler.cs          # Enforces payload.PeerId == senderId
└── DiscoveryPlugin.cs                      # IAppPlugin (AppId: 0x02)
```

### 2.1 Value Objects (Zero Cast Operators)
- `DiscoveryResultLimit`: Encapsulates result bounds ($1 \le \text{limit} \le 100$). Provides explicit `From(int)`, `Default`, and `Value`. Zero `implicit` or `explicit` operators.
- `BlindedLocator`: Encapsulates 32-byte cryptographic token. Provides `Create(byte[] sharedKey, DiscoveryEpoch epoch)` and `Bytes`. Zero `implicit` or `explicit` operators.
- `DiscoveryEpoch`: Encapsulates 24-hour UTC Unix day ratchet window. Provides `FromDateTimeOffset(DateTimeOffset)` and `EpochNumber`. Zero `implicit` or `explicit` operators.

---

## 3. Threat Model & Privacy Invariants

| Threat / Vulnerability | Protocol Defense | Verification Mechanism |
| :--- | :--- | :--- |
| **One-Time PreKey (OPK) Starvation** | Two-Phase Discovery: Step 1 queries candidate descriptors only. OPKs are retrieved exclusively in Step 2 on explicit user intent to connect. | Unit test asserts prekey bundle provider is uninvoked during Step 1 query. |
| **Directory Spam & Sybil Flooding** | Hard Admission Gate: Relay requires both mutual handshake completion and an active Signed PreKey before directory registration. | Admission gate test matrix asserts rejection when SPK is missing or expired. |
| **Impersonation Attacks** | Envelope Authenticity: Ingress handler asserts `payload.PeerId == context.SenderIdentityId`. | Ingress handler test asserts rejection when payload peer ID does not match sender ID. |
| **Temporal Identity Correlation** | Blinded Locators: Rotating HMAC-SHA256 handles across 24-hour UTC epoch windows. | Unit test asserts identical keys generate distinct locators across different epochs. |
| **IP Endpoint Exposure** | Route Masking: WAN candidates return `DeliveryRoute.RelayedOneToOne` with relay tokens instead of public IP endpoints. | Descriptor deserialization test asserts relayed route assignment. |

---

## 4. High-Value, Low-Brittle Unit Testing Matrix

| Test Category | Target Feature / State | Verifies (Observable Behavior) | Low-Brittle Guarantee |
| :--- | :--- | :--- | :--- |
| **Admission Gate** | Valid SPK + Handshake Complete | Returns `DiscoveryAdmissionResult.Admitted` | Asserts typed result enum; zero mock call verification. |
| **Admission Gate** | Missing or Expired Signed PreKey | Returns `RejectedMissingSignedPreKey` | Asserts typed rejection reason; no string matching. |
| **Admission Gate** | Handshake Incomplete | Returns `RejectedHandshakeIncomplete` | Asserts typed rejection reason. |
| **Directory Policy** | Policy = `Disabled` | Returns empty candidate list | Asserts count == 0; ignores internal store mechanics. |
| **Directory Policy** | Peer Visibility = `Hidden` | Excludes peer from nearby queries | Asserts hidden peer is absent from returned candidates. |
| **Private Rendezvous** | Same Key + Same Epoch | Generates identical `BlindedLocator` | Asserts byte equality of locator output. |
| **Private Rendezvous** | Same Key + Different Epoch | Generates distinct `BlindedLocator` | Asserts inequality across epoch transition. |
| **Two-Phase Workflow** | Step 1 Candidate Discovery | Returns candidate descriptors | Asserts `IPreKeyBundleProvider` was never invoked. |
| **Two-Phase Workflow** | Step 2 Connection Dial | Pops OPK bundle and handshakes | Asserts successful session result returned. |
| **Two-Phase Workflow** | Partial Dial Failure in Batch | Succeeds on remaining peers | Asserts output collection contains successful dials without throwing. |
| **Anti-Impersonation** | Payload ID != Envelope Sender ID | Rejects incoming registration frame | Asserts handler returns rejection / throws `SecurityException`. |

---

## 5. Cross-Layer Infrastructure Alignment

| Application Port / Contract | Infrastructure Adapter / Concrete Artifact | Target Persistence / Transport |
| :--- | :--- | :--- |
| `IRelayPeerDirectory` | `SqliteRelayPeerDirectory` | `disc_relay_peer_registrations` table |
| `INearbyDiscoveryQueryClientPort` | `DiscoveryQueryClient` | gRPC `discovery.proto` client |
| `IPreKeyAvailabilityPort` | `PreKeyAvailabilityAdapter` | Local prekey vault / directory check |
| `IPreKeyBundleProvider` | `PreKeyBundleProviderAdapter` | Out-of-band `AuthenticatedRelayService.FetchPreKeyBundle` |
| `IDiscoveryHandshakePort` | `DiscoveryHandshakeAdapter` | Bridges to `IHandshakeWorkflow` in `Application` |
| Pruning & Retention | `RelayDiscoveryRegistrationPruner` | Scheduled cleanup worker for expired relay entries |
| Wire Serialization | `ProtobufPayloadSerializer` | `discovery.proto` (Query, Response, Descriptor) |

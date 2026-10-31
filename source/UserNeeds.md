# Percolator User Needs

Spec: [user-needs-spec](spec/user-needs-spec.md)

## 1. Core Functional Needs

1. **`UN-PERSONAS` (Compartmentalized Personas):** [`USR-END-USER`](IntendedUse.md#2-target-user-profiles)s need to create, switch between, and retire separate identities on their device for distinct social or professional contexts (per [`IU-SOVEREIGN-ID`](IntendedUse.md#1-intended-purpose)).
2. **`UN-MULTI-DEVICE` (Multi-Device Identity Operation):** Users need to operate the same identity across multiple personal devices with a synchronized view of their contacts and conversations.
3. **`UN-CONVERSATIONS` (Direct and Group Conversations):** Users need to hold private one-to-one and multi-party conversations across [`ENV-NETWORK`](IntendedUse.md#3-operating-environment), including adding and removing group participants so that removed members are excluded from future group conversation. `#group-membership`
4. **`UN-ASYNC-MESSAGING` (Asynchronous and Indirect Messaging):** Users need to send and receive messages reliably even when communicating peers are not online at the same time or cannot establish a direct network connection. `#offline-delivery`

---

## 2. Sovereignty, Privacy, and Operational Trust Needs

1. **`UN-CONTACT-VERIFICATION` (Contact Authenticity Verification):** [`USR-END-USER`](IntendedUse.md#2-target-user-profiles)s need a straightforward way to verify a contact's authenticity out-of-band and inspect the current trust status of any conversation participant without needing cryptographic expertise.
2. **`UN-ADDRESS-PRIVACY` (Peer-Level Network Address Privacy):** Users need the ability to communicate with unverified or selected peers without disclosing their direct network address to those peers. `#relay-privacy`
3. **`UN-PEER-BLOCKING` (Selective Peer Blocking):** Users need the ability to unilaterally block a specific remote identity, stopping inbound communications and hiding their online presence from that peer.
4. **`UN-SESSION-LOCK` (Explicit Session Activation and Lock):** Users need explicit control over when an identity is unlocked and active on the network, with the ability to lock the session at any time to immediately halt network presence and secure local data (per [`IU-PROTECTED-LOCAL-STATE`](IntendedUse.md#1-intended-purpose)). `#session-lock`
5. **`UN-DATA-RETENTION` (Manual and Automated History Retention):** Users need to manually delete local messages, conversations, or identities, and configure conversations to automatically purge messages after a defined period across participating devices (within [`CONTRA-POST-DELIVERY-DRM`](IntendedUse.md#4-contraindications-and-out-of-scope-uses) bounds). `#ephemeral-retention`
6. **`UN-BOUNDED-RELAY` (Zero-Touch Bounded Relay Participation):** Peers acting as [`USR-RELAY-OPERATOR`](IntendedUse.md#2-target-user-profiles)s need to contribute background connectivity and offline delivery assistance automatically—without manual server or network administration—while keeping local bandwidth, storage, and compute usage within predictable, user-defined limits.

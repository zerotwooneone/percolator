# Percolator Intended Use

Spec: [intended-use-spec](spec/intended-use-spec.md)

## 1. Intended Purpose

Percolator is a decentralized, peer-to-peer (P2P) communication and data-sharing system. Its purpose is to enable individuals and groups to establish contact, converse, and exchange data directly across untrusted networks without relying on centralized identity authorities, cloud storage providers, or trusted third-party servers.

The system is intended to provide:
- **`IU-SOVEREIGN-ID` (Self-Sovereign Identity):** Users establish and control their own identities locally without central registration or external account bindings (such as phone numbers or email addresses).
- **`IU-E2EE-COMMUNICATION` (Confidential and Authentic Communication):** Interpersonal and group communications are protected end-to-end so that only intended participants can read content or verify its origin.
- **`IU-METADATA-MINIMIZATION` (Minimal Metadata Exposure):** Peer connection, routing, and offline delivery minimize the exposure of social graphs and communication patterns to network observers and intermediate nodes. `#relay-privacy`
- **`IU-PROTECTED-LOCAL-STATE` (Protected Local State):** Sensitive local data and credentials remain protected at rest when the application is not actively in use, and no network presence is exposed without explicit user intent. `#session-lock`

---

## 2. Target User Profiles

1. **`USR-END-USER` (Communicating Peers):**
   - **Role:** Individuals who use the system to manage identities, establish trust with contacts, participate in direct or group conversations, and share files.
   - **Assumptions:** Possess general computing literacy and control over their device during active use, but are not expected to have expertise in cryptography or network administration. They rely on the system to handle security mechanics automatically and to present clear, actionable warnings when trust assumptions change.
2. **`USR-RELAY-OPERATOR` (Untrusted Relay Contributors):**
   - **Role:** Peers or node operators that contribute network bandwidth, connectivity assistance, or temporary store-and-forward capacity for offline peers.
   - **Assumptions:** Treated as **untrusted** (potentially curious or actively malicious). Relay contributors have no administrative authority over users and must never be able to read communication content, forge messages, or alter trust relationships. `#relay-privacy`

---

## 3. Operating Environment

1. **`ENV-ENDPOINT` (Endpoint Environment):**
   - Personal computing devices where the host operating system, process isolation, and hardware input/display paths are intact while the user is actively operating the software.
   - Persistent storage is treated as untrusted when the application is closed, locked, or unattended (e.g., subject to offline disk inspection or device theft).
2. **`ENV-NETWORK` (Adversarial Network Environment):**
   - Untrusted local and wide-area networks subject to passive surveillance, traffic correlation, and active packet manipulation (interception, replay, delay, modification, or dropping).
   - Peers operate across consumer NATs, firewalls, dynamic network addresses, and intermittent connectivity, coming online and offline asynchronously without guaranteed always-on server infrastructure.

---

## 4. Contraindications and Out-of-Scope Uses

1. **`CONTRA-COMPROMISED-HOST` (Compromised Active Endpoint):**
   - The system cannot protect identities or communications on a device whose operating system, runtime memory, or input/display hardware is compromised by malware, rootkits, or keyloggers while the software is actively unlocked.
2. **`CONTRA-GLOBAL-TRAFFIC-ANONYMITY` (Network-Layer Anonymity Against Global Traffic Analysis):**
   - The system is a direct peer-to-peer communication tool, not an onion-routing mixnet (such as Tor or I2P). It must not be relied upon to conceal network-layer addresses (such as IP addresses) from an adversary capable of monitoring both communicating endpoints simultaneously.
3. **`CONTRA-KEY-ESCROW` (Centralized Compliance, Key Escrow, or Account Recovery):**
   - The system intentionally omits administrative backdoors, third-party key escrow, and centralized account recovery. If a user loses their local credentials and keys, access cannot be restored by any third party; the system is contraindicated for environments legally mandating non-consensual administrative interception or escrow.
4. **`CONTRA-LIFE-SAFETY` (Hard Real-Time or Life-Safety Signaling):**
   - Because peer-to-peer connectivity operates over best-effort networks subject to partitions and packet loss, the system does not guarantee bounded delivery latency and must not be used for safety-critical telemetry, emergency dispatch, or life-support control.
5. **`CONTRA-POST-DELIVERY-DRM` (Post-Delivery Content Control / DRM):**
   - Once data is decrypted by an intended recipient, that recipient controls the plaintext. Local data-hygiene features cannot prevent a malicious or compromised recipient from copying, recording, or redistributing received content outside the system.

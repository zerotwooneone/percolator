# Percolator Risk Register

Spec: [risk-register-spec](spec/risk-register-spec.md)

## 1. Cryptographic, Identity, and Privacy Hazards

1. **`HAZ-SESSION-KEY-COMPROMISE` (Transient Session State Compromise):**
   - **Cause & Harm:** Compromise of a single ephemeral session key under [`ENV-ENDPOINT`](IntendedUse.md#3-operating-environment) or [`ENV-NETWORK`](IntendedUse.md#3-operating-environment) exposes past recorded conversation history or allows indefinite decryption of future messages (violating [`IU-E2EE-COMMUNICATION`](IntendedUse.md#1-intended-purpose)).
   - **Initial Risk:** `S4 / L2` | **Residual Risk:** `S3 / L1` (`Controlled` — inherent per-message key progression bounds blast radius to a single message rather than the full conversation history).
2. **`HAZ-REMOVED-MEMBER-EAVESDROPPING` (Post-Removal Group Eavesdropping):**
   - **Cause & Harm:** A participant removed from a group conversation ([`UN-CONVERSATIONS`](UserNeeds.md#1-core-functional-needs)) retains prior group key material and intercepts subsequent group traffic via [`ENV-NETWORK`](IntendedUse.md#3-operating-environment) or a colluding [`USR-RELAY-OPERATOR`](IntendedUse.md#2-target-user-profiles).
   - **Initial Risk:** `S3 / L3` | **Residual Risk:** `S3 / L1` (`Controlled`). `#group-membership`
3. **`HAZ-KEY-SUBSTITUTION-MITM` (Identity Key Substitution / Man-in-the-Middle):**
   - **Cause & Harm:** An active attacker on [`ENV-NETWORK`](IntendedUse.md#3-operating-environment) or a malicious [`USR-RELAY-OPERATOR`](IntendedUse.md#2-target-user-profiles) substitutes a peer's public identity keys during contact establishment or mid-relationship, impersonating the contact to [`USR-END-USER`](IntendedUse.md#2-target-user-profiles).
   - **Initial Risk:** `S4 / L3` | **Residual Risk:** `S4 / L1` (`Controlled`).
4. **`HAZ-CIPHERTEXT-SIZE-CORRELATION` (Payload Length Traffic Analysis):**
   - **Cause & Harm:** Passive observers on [`ENV-NETWORK`](IntendedUse.md#3-operating-environment) or [`USR-RELAY-OPERATOR`](IntendedUse.md#2-target-user-profiles) nodes infer interaction types or message content from exact ciphertext byte lengths (violating [`IU-METADATA-MINIMIZATION`](IntendedUse.md#1-intended-purpose)).
   - **Initial Risk:** `S3 / L3` | **Residual Risk:** `S3 / L1` (`Controlled`). `#relay-privacy`
5. **`HAZ-RELAY-HEADER-CORRELATION` (Social-Graph Leakage via Relay Envelopes):**
   - **Cause & Harm:** Untrusted [`USR-RELAY-OPERATOR`](IntendedUse.md#2-target-user-profiles) nodes inspect buffered or routed envelopes to identify the sender and map social graphs (violating [`IU-METADATA-MINIMIZATION`](IntendedUse.md#1-intended-purpose) and [`UN-ADDRESS-PRIVACY`](UserNeeds.md#2-sovereignty-privacy-and-operational-trust-needs)).
   - **Initial Risk:** `S3 / L3` | **Residual Risk:** `S3 / L1` (`Controlled`). `#relay-privacy`
6. **`HAZ-PRESENCE-AND-BLOCK-ORACLE` (Unapproved or Blocked Peer Presence Probing):**
   - **Cause & Harm:** A blocked or unknown remote identity sends unsolicited packets across [`ENV-NETWORK`](IntendedUse.md#3-operating-environment) to determine whether a target identity is online or has blocked them (violating [`UN-PEER-BLOCKING`](UserNeeds.md#2-sovereignty-privacy-and-operational-trust-needs)).
   - **Initial Risk:** `S3 / L3` | **Residual Risk:** `S3 / L1` (`Controlled`).
7. **`HAZ-TRANSPORT-SPOOFING` (Unauthenticated Wire Channel Injection):**
   - **Cause & Harm:** An active attacker on [`ENV-NETWORK`](IntendedUse.md#3-operating-environment) hijacks or injects frames into a peer-to-peer or relay transport connection.
   - **Initial Risk:** `S3 / L3` | **Residual Risk:** `S3 / L1` (`Controlled`).
8. **`HAZ-COMPROMISED-SECONDARY-DEVICE` (Loss or Compromise of an Authorized Multi-Device Endpoint):**
   - **Cause & Harm:** One personal device in a multi-device identity ([`UN-MULTI-DEVICE`](UserNeeds.md#1-core-functional-needs)) is lost or stolen, allowing an attacker with access to that device to continue receiving synchronized conversations.
   - **Initial Risk:** `S4 / L2` | **Residual Risk:** `S3 / L1` (`Controlled` — device revocation bounds exposure to prior synced state rather than permanent identity loss).

---

## 2. Endpoint, Availability, and Integrity Hazards

1. **`HAZ-PRE-UNLOCK-NETWORK-EXPOSURE` (Network Presence Leakage Prior to Identity Activation):**
   - **Cause & Harm:** Launching the application binds listening sockets or emits network packets before explicit user unlock and identity activation (violating [`IU-PROTECTED-LOCAL-STATE`](IntendedUse.md#1-intended-purpose) and [`UN-SESSION-LOCK`](UserNeeds.md#2-sovereignty-privacy-and-operational-trust-needs)).
   - **Initial Risk:** `S3 / L2` | **Residual Risk:** `S3 / L1` (`Controlled`). `#session-lock`
2. **`HAZ-POST-LOCK-MEMORY-RESIDUE` (Residual Connections or Keys After Session Lock):**
   - **Cause & Harm:** Locking or closing an active session ([`UN-SESSION-LOCK`](UserNeeds.md#2-sovereignty-privacy-and-operational-trust-needs)) leaves active network sockets open or leaves decrypted keys and plaintext resident in volatile RAM.
   - **Initial Risk:** `S4 / L2` | **Residual Risk:** `S4 / L1` (`Controlled`). `#session-lock`
3. **`HAZ-OFFLINE-DISK-EXTRACTION` (Forensic Extraction of Unattended Storage):**
   - **Cause & Harm:** Physical theft or offline disk inspection of [`ENV-ENDPOINT`](IntendedUse.md#3-operating-environment) while the application is locked or closed exposes stored private identity keys, contacts, or message histories (violating [`IU-PROTECTED-LOCAL-STATE`](IntendedUse.md#1-intended-purpose)).
   - **Initial Risk:** `S4 / L2` | **Residual Risk:** `S4 / L1` (`Controlled`). `#session-lock`
4. **`HAZ-INGRESS-RESOURCE-EXHAUSTION` (Remote Compute, Memory, and Storage Exhaustion DoS):**
   - **Cause & Harm:** Remote attackers flood a peer or [`USR-RELAY-OPERATOR`](IntendedUse.md#2-target-user-profiles) with replayed handshakes, oversized frames, or unbounded relay storage requests, exhausting local CPU, memory, or disk space (violating [`UN-BOUNDED-RELAY`](UserNeeds.md#2-sovereignty-privacy-and-operational-trust-needs)).
   - **Initial Risk:** `S2 / L3` | **Residual Risk:** `S2 / L1` (`Controlled`).
5. **`HAZ-PREKEY-POOL-STARVATION` (Asynchronous One-Time Pre-Key Exhaustion):**
   - **Cause & Harm:** An attacker rapidly drains a peer's published one-time pre-key pool on relays, preventing legitimate contacts from establishing offline sessions under [`UN-ASYNC-MESSAGING`](UserNeeds.md#1-core-functional-needs).
   - **Initial Risk:** `S3 / L3` | **Residual Risk:** `S3 / L1` (`Controlled`). `#offline-delivery`
6. **`HAZ-CRASH-STATE-DESYNC` (Non-Atomic Crash Between Cryptographic Step and Outbound Queue):**
   - **Cause & Harm:** Process termination, power loss, or transient link failure occurs after advancing cryptographic session state but before the outbound message is durably queued, causing silent message loss or permanent session desynchronization.
   - **Initial Risk:** `S3 / L2` | **Residual Risk:** `S3 / L1` (`Controlled`). `#offline-delivery`

 # Signal session flows: models, persistence, security

 This document describes Signal/X3DH bootstrapping and Double Ratchet session flows, focusing on:
 - Protobuf wire models
 - Client-side persistence (SQLite-like) for pending and established sessions
 - Security and privacy considerations

 Sections
 - Part 1: Standard Flow (Alice initiates; Bob may be offline)
 - Part 2: Reverse-Signal Flow (Inviter invites Acceptor to initiate)
 - Part 3: Ongoing Session Persistence

 ---

 ## Part 1: Standard Flow (Alice Initiates with Offline Bob)

 Bob publishes a pre-key bundle to a pre-key service (relay/server). Alice fetches Bob’s bundle from that service and then sends Bob a pre-key message (initiator hello). Alice finalizes the session after Bob’s first ratchet message.

 Assumptions (standard Signal does not define these):
 - Alice learns how to locate Bob’s pre-key bundle **out of band** (e.g., she already knows Bob’s identity key fingerprint / service user id / address).
 - Message delivery of Alice’s pre-key message to Bob can be via a relay/service or direct transport; the cryptographic flow is the same.

 ### Step 1.1: Fetch Pre-Key Bundle and Persist Pending Session

 Alice fetches Bob’s published pre-key bundle from a pre-key service/relay and derives the Initial Root Key (IRK). Persist a short‑lived pending record.

 ```protobuf
 // Alice asks the pre-key service for Bob's published bundle.
 // The lookup key (identity key, user id, etc.) is assumed to be obtained out-of-band.
 message GetPreKeyBundleRequest {
   bytes recipient_identity_key = 1;
 }

 // The service returns Bob's public keys.
 message GetPreKeyBundleResponse {
   bytes recipient_identity_key = 1;
   bytes recipient_signed_pre_key = 2;
   bytes pre_key_signature = 3;
   optional bytes recipient_one_time_pre_key = 4;
 }
 ```

 SQLite (Alice, PendingSessions)

 | Column              | Type     | Description                                                                |
 | ------------------- | -------- | -------------------------------------------------------------------------- |
 | id                  | INTEGER  | Primary Key                                                                |
 | remote_identity_key | BLOB     | Indexed. Bob’s identity key (SPKI).                                        |
 | initial_root_key    | BLOB     | Initial Root Key (IRK) derived from X3DH; encrypted at rest; short‑lived.  |
 | created_at          | DATETIME | For cleanup of stale pending sessions.                                     |

 Security
 - Do not store Alice’s ephemeral private key. Derive IRK, then discard the ephemeral private key immediately.
 - Encrypt IRK at rest, purge pending records aggressively.

 ### Step 1.2: Send Pre-Key Message

 Alice sends Bob the “pre-key message” (initiator’s first message) that references Bob’s pre-keys and includes Alice’s identity + ephemeral public keys.

 ```protobuf
 // Alice's first message to Bob (the "Pre-Key Message")
 message InitiatorHandshakeMessage {
   bytes initiator_identity_key                = 1; // Alice's identity
   bytes initiator_ephemeral_key               = 2; // Alice's X3DH ephemeral pubkey
   string responder_signed_pre_key_id          = 3; // Bob's SPK id used
   optional string responder_one_time_pre_key_id = 4; // Bob's OTK id if used

   // Optional: first application ciphertext encrypted from a key derived off IRK
   bytes initial_ciphertext                    = 5;
 }
 ```

 ### Step 1.3: Bob Establishes and Responds

 ```protobuf
 message RatchetMessage {
   RatchetHeader header = 1;   // unencrypted header
   bytes encrypted_payload = 2;
 }

 message RatchetHeader {
   // Bob's next DH ratchet public key (unencrypted)
   bytes public_ratchet_key = 1;
   uint32 message_counter = 2;
   uint32 previous_chain_length = 3;
 }

 // After Alice decrypts the payload via the initialized session
 message DecryptedPayload {
   string session_id = 1;        // Official, responder-assigned Session ID
   string message_content = 2;   // Optional first message content
 }
 ```

 ### Step 1.4: Alice Finalizes the Session

 - Fast-path: Try to match `public_ratchet_key` to an existing session. It will miss (first message).
 - Slow-path: Iterate all pending records for the active identity (ordered by recency).
   - For each pending record:
     - Recompute the expected initial state using the stored IRK and the header’s public_ratchet_key.
     - Attempt to decrypt the responder’s first ratchet message.
     - If decrypt succeeds:
       - Extract session_id from the decrypted inner payload (do not derive).
       - Create and persist the initiator’s full session using this session_id.
       - Upsert the ratchet-key index for the header’s public_ratchet_key.
       - Delete this pending record.
       - Stop.
   - If all pending records fail to decrypt, treat as a miss (no state changes).

 Privacy/Security
 - Do not derive session IDs. Always use the responder-provided `session_id`.
 - Zeroize derived key material in memory when no longer needed.

 ---

 ## Part 2: Reverse-Signal Flow (Inviter invites Acceptor to initiate)

 Alice (the inviter) invites Bob (the acceptor) to initiate a session with her. This can be via a relay/host.

Note
- Less private than Standard Flow (Alice reveals bundle first). Consider product UX tradeoffs.

### Step 2.1: Inviter Sends Invitation

```protobuf
// Opaque to the transport. The inviter is the X3DH responder; the acceptor is the X3DH initiator.
message InviteHandshakePreKeyBundle {
  uint32 version = 1;
  bytes inviter_signed_pre_key = 2;
  bytes pre_key_signature = 3;
  optional bytes inviter_one_time_pre_key = 4;
}

message InviteHandshakeRequestPayload {
  uint32 version = 1;
  // Hostname or IP literal.
  string inviter_host = 2;
  uint32 inviter_port = 3;
  InviteHandshakePreKeyBundle inviter_pre_key = 4;
  google.protobuf.Timestamp expires_at_utc = 5;

  // must be unpredictable (GUID). Acceptor stores seen request_correlation_id until expires_at_utc and rejects duplicates
  string request_correlation_id = 6;
}

// RPC ingress request
message EstablishDirectSessionRequest {
  uint32 version = 1;

  // Inviter identity key (SPKI)
  bytes inviter_identity_key = 2;

  // always a serialized InviteHandshakeRequestPayload
  bytes payload = 3;

  // signed by inviter_identity_key over raw payload bytes
  bytes payload_signature = 4;
}
```

SQLite (Inviter, SentInvitations)

| Column                     | Type     | Description                                                       |
| -------------------------- | -------- | ----------------------------------------------------------------- |
| request_correlation_id     | VARCHAR  | Primary Key. Inviter-generated id for correlation.                |
| inviter_signed_pre_key_id  | UUID     | Inviter-local id of the signed pre-key used for this invite.      |
| inviter_one_time_pre_key_id| UUID     | Inviter-local id of the reserved/used OTK (nullable).             |
| target_peer_id             | UUID     | Who the invite is for (nullable depending on send path).          |
| created_at                 | DATETIME | For cleanup.                                                      |
| expires_at_utc             | DATETIME | TTL/expiry for cleanup.                                           |

Security
- Do not persist any private key material in SentInvitations.
- parse payload only after verification
- verify pre_key_signature using the same inviter_identity_key over inviter_signed_pre_key
- validate host/port under policy (allow_LAN, port range, etc.)
- reject if request_correlation_id already seen and not expired
- verify payload_signature over raw payload bytes as received

### Step 2.2: Acceptor Queues for Approval

Bob (the acceptor) receives the invite and awaits explicit consent.

SQLite (Acceptor, PendingInvitations)

| Column               | Type     | Description                                  |
| -------------------- | -------- | -------------------------------------------- |
| request_correlation_id | VARCHAR  | From `request_correlation_id`.                  |
| inviter_identity_key | BLOB     | Inviter’s identity key (for UI/verification).  |
| invitation_blob      | BLOB     | Serialized invite payload. serialized EstablishDirectSessionRequest |
| status               | TEXT     | e.g., “AwaitingUserApproval”.                |

### Step 2.3: Acceptor Accepts and Responds

```protobuf
message InviteHandshakeResponse {
  string request_correlation_id = 1;  // Echo back to inviter

  // Acceptor acts as X3DH initiator now
  bytes acceptor_identity_key       = 2;
  bytes acceptor_x3dh_ephemeral_key = 3;

  // First ratchet message (header has the acceptor's next ratchet key)
  bytes initial_ratchet_message = 4;
}
```

- Acceptor establishes their side immediately; persists full session (see Part 3).
- Acceptor deletes `PendingInvitations` record.

### Step 2.4: Inviter Finalizes

- Lookup `SentInvitations` by `request_correlation_id`.
- Complete X3DH on inviter side; initialize double ratchet; decrypt `initial_ratchet_message`; retrieve `session_id`.
- Persist full session (Part 3) and delete `SentInvitations` record.

---

## Part 3: Ongoing Session Persistence (Both Peers)

 SQLite (Sessions)

 | Column               | Type     | Description                                           |
 | -------------------- | -------- | ----------------------------------------------------- |
 | session_id           | VARCHAR  | Primary Key. Official responder-assigned id.          |
 | remote_identity_key  | BLOB     | Indexed. Remote peer’s identity key (SPKI).          |
 | root_key             | BLOB     | Current root key.                                     |
 | sending_chain_key    | BLOB     | Current sending chain key.                            |
 | sending_counter      | INTEGER  | Sending chain counter.                                |
 | receiving_chain_key  | BLOB     | Current receiving chain key.                          |
 | receiving_counter    | INTEGER  | Receiving chain counter.                              |
 | ...other_state       | ...      | e.g., previous_chain_length, etc.                     |

 SQLite (RemoteRatchetKeys)

 | Column             | Type     | Description                              |
 | ------------------ | -------- | ---------------------------------------- |
 | public_ratchet_key | BLOB     | Primary Key. Current remote ratchet key. |
 | session_id         | VARCHAR  | FK to Sessions.                          |

 Operational Notes
 - Encrypt sensitive fields at rest (keys, IRK).
 - Sanitize logs; never log keys or session ids.
 - TTL/purge for pre-handshake stores and invite stores (minutes/hours).
 - Defensive parsing/versioning on all protobufs.

# Part 4: The X3DH Handshake (Initiator Logic)

The Initiator (Alice) is responsible for calculating the initial **Shared Secret (SK)**. This secret becomes the "Root Key" for the Double Ratchet session. Alice performs this calculation using her own keys and the pre-keys she fetched from the server for Bob.

### 4.1. Required Keys
Alice collects the following keys to begin the calculation:

* **$IK_A$:** Alice's Identity Key (Private).
* **$EK_A$:** Alice's Ephemeral Key (Private). *Generated specifically for this handshake.*
* **$IK_B$:** Bob's Identity Key (Public). *From bundle.*
* **$SPK_B$:** Bob's Signed Pre-Key (Public). *From bundle.*
* **$OPK_B$:** Bob's One-Time Pre-Key (Public). *From bundle (Optional, but recommended).*

### 4.2. The Diffie-Hellman Operations
Alice calculates the shared secret by mixing these keys using Elliptic Curve Diffie-Hellman (ECDH).



1.  **DH1 = $DH(IK_A, SPK_B)$**: Mutual authentication component.
2.  **DH2 = $DH(EK_A, IK_B)$**: Authorizes the ephemeral session.
3.  **DH3 = $DH(EK_A, SPK_B)$**: Binds the ephemeral key to the signed pre-key.
4.  **DH4 = $DH(EK_A, OPK_B)$**: *Only if OPK exists.* Provides Forward Secrecy.

### 4.3. Key Derivation (KDF)
The final Shared Secret ($SK$) is derived by concatenating the results and passing them through a Key Derivation Function (HKDF).

$$SK = HKDF( \text{pad} || DH1 || DH2 || DH3 || DH4 )$$

*Note: If using Curve25519, a specific byte sequence of `0xFF` is often prepended as padding.*

---

# Part 5: The X3DH Handshake (Responder Logic)

The Responder (Bob) calculates the **same** Shared Secret ($SK$) upon receiving Alice's initial message. Bob does not know the secret exists until he receives the message header containing Alice's public keys.

### 5.1. Required Keys
Bob retrieves the following from his storage and the incoming message header:

* **$IK_B$:** Bob's Identity Key (Private).
* **$SPK_B$:** Bob's Signed Pre-Key (Private). *Identified by ID in the header.*
* **$OPK_B$:** Bob's One-Time Pre-Key (Private). *Identified by ID in the header.*
* **$IK_A$:** Alice's Identity Key (Public). *From message header.*
* **$EK_A$:** Alice's Ephemeral Key (Public). *From message header.*

### 5.2. The Mirror Calculation
Bob performs the exact same ECDH operations, but using the corresponding private keys on his side.

1.  **DH1 = $DH(SPK_B, IK_A)$**
2.  **DH2 = $DH(IK_B, EK_A)$**
3.  **DH3 = $DH(SPK_B, EK_A)$**
4.  **DH4 = $DH(OPK_B, EK_A)$**

### 5.3. Result
Bob runs the same KDF:
$$SK = HKDF( \text{pad} || DH1 || DH2 || DH3 || DH4 )$$

If successful, Bob's $SK$ will match Alice's $SK$ byte-for-byte. This $SK$ is immediately promoted to be the **Root Key** of the new session.

---

# Part 6: Sending Messages (The Double Ratchet)

Once the session is established (Root Key initialized), sending a message involves "ratcheting" the keys forward. This ensures that every message has a unique encryption key.



### 6.1. The Chain Key KDF (Symmetric Ratchet)
Every session has a **Sending Chain Key**. To send a message:

1.  **Input:** Current Sending Chain Key.
2.  **KDF Output:** The KDF produces 64 bytes (if using 32-byte keys).
 * **Bytes 0-31:** Become the **Message Key**.
 * **Bytes 32-63:** Become the **Next Chain Key**.

### 6.2. Encryption
1.  **Encrypt:** The payload is encrypted using AES-GCM (or similar AEAD) with the **Message Key**.
2.  **Update State:** The old Chain Key is deleted and replaced by the *Next Chain Key*.
3.  **Header:** The sender includes their current **Ratchet Public Key** in the header.

### 6.3. The DH Ratchet (Updates Root Key)
If the sender receives a reply with a *new* Ratchet Key from the other party, a DH Ratchet step occurs **before** the Symmetric step above.
1.  Generate new ephemeral keypair.
2.  $DH_{out} = DH(NewPair_{priv}, Remote_{pub})$.
3.  Derive new **Root Key** and new **Sending Chain Key** from $DH_{out}$.

---

# Part 7: Receiving Messages (Decryption)

When a client receives a `RatchetMessage`, it must derive the specific key used to encrypt that specific message.

### 7.1. Determining the Path
The client reads the **Ratchet Public Key** from the unencrypted header.
* **Same Key:** If the key matches the current remote key, use the **Symmetric Ratchet** (Fast Path).
* **New Key:** If the key is different, perform a **DH Ratchet** (Slow Path).

### 7.2. DH Ratchet (If New Key Detected)
1.  $DH_{in} = DH(MyRatchet_{priv}, HeaderKey_{pub})$.
2.  Use KDF to update the **Root Key**.
3.  Derive a new **Receiving Chain Key**.

### 7.3. Symmetric Ratchet (Deriving the Message Key)
Using the current **Receiving Chain Key**:
1.  Run KDF to output 64 bytes.
2.  **Bytes 0-31:** The **Message Key** for this message.
3.  **Bytes 32-63:** The **Next Receiving Chain Key**.

### 7.4. Decryption
The client uses the derived **Message Key** to decrypt the payload.
* **Success:** The message is readable. The *Next Receiving Chain Key* is saved. The Message Key is deleted.
* **Failure:** The state is rolled back (transactional).

# Part 8: Group Conversations (Signal Group V2 / zkgroup)

While 1:1 messaging uses an asymmetric Double Ratchet to manage end-to-end sessions between individual devices, group conversations utilize **Signal Group V2 (`zkgroup`)**. Instead of fanning out individual sender keys or broadcasting plaintext member arrays via 1:1 sessions, Group V2 manages shared state through a centralized server architecture that is secured by **Zero-Knowledge (ZK) anonymous credentials** and client-side deterministic key derivation.

The server acts as a validated coordinator, but client cryptography ensures that the server never learns the group name, the membership roster, or the content being distributed.

---

## 8.1 The Group Root Secret (`GroupMasterKey`)

A group conversation is structurally anchored by a single 32-byte root secret called the `GroupMasterKey`. The server never sees this key; it exists exclusively in the local, encrypted storage of the authorized participants.

### 8.1.1 Cryptographic Key Derivation Sequence

Upon group creation or local rehydration, the 32-byte `GroupMasterKey` is passed into a native key derivation function (KDF) to deterministically squeeze out a single `GroupSecretParams` structure containing four critical primitives:

$$GroupSecretParams = \text{Derive}(GroupMasterKey)$$

- **`group_id` (Group Identifier):** A unique, stable byte array used by the server and clients to identify the cryptographic context of the group.
- **`blob_key` (Encryption Key):** A symmetric AES key used to encrypt and decrypt the group's profile metadata, such as the title, avatar, and active membership roster.
- **`uid_enc_key_pair` (User ID Encryption Keypair):** An asymmetric keypair used to obfuscate user identities inside the group context.
- **`profile_key_enc_key_pair` (Profile Encryption Keypair):** An asymmetric keypair used to manage secure access to individual member profile keys within the group.

Because this derivation is entirely deterministic, clients only need to store the encrypted 32-byte `GroupMasterKey` at rest. All sub-keys, identifiers, and encryption pairs are fully rehydrated in memory on demand.

---

## 8.2 Server-Assisted Routing via Zero-Knowledge Credentials

In Group V1, the server was completely blind, forcing clients to manually track and distribute keys to every recipient. In Group V2, the server actively manages the group's state and enforces access controls, but it is cryptographically blinded via zero-knowledge proofs.

### 8.2.1 Group Presentation and Admission
When a user wishes to interact with a group (e.g., fetching the encrypted member list or sending a message envelope), they do not authenticate with their account identity or present a plaintext membership token.

Instead, the client uses the derived `GroupSecretParams` to construct a short-lived **Zero-Knowledge Presentation Proof**.

```protobuf
// Conceptual schema for interacting with the Group server
message GroupServiceRequest {
  bytes group_id                       = 1; // Derived from GroupSecretParams
  bytes zero_knowledge_member_proof   = 2; // ZK proof of valid membership
  bytes encrypted_action_payload       = 3; // Opaque action executed on the server
}
```

#### The Client's Action

The client generates a mathematical proof demonstrating the statement: "I possess a valid credential signed by the group's administrative keys showing I am an active member of this group identifier."

#### The Server's Validation

The server verifies the proof mathematically. If the proof is valid, the server authorizes the request (e.g., accepting an uploaded message or returning an updated metadata blob).

#### The Privacy Invariant

The server learns absolutely nothing about who the user is, which other members belong to the group, or what text/media is inside the group envelope. It only learns that a cryptographically valid group member performed an action.

## 8.3 Inbound and Outbound Message Pipeline

Group messaging content is protected by an authenticated, symmetric envelope layer tied directly to the derived parameters, running alongside traditional 1:1 transport infrastructure for fallback operations where required.

### 8.3.1 Outbound Send Path

The client loads the 32-byte GroupMasterKey from the local database, decrypts it, and rehydrates the GroupSecretParams.

The message payload (text, attachments) is encrypted symmetrically using the derived blob_key.

The client constructs a presentation proof using its native ZK primitives to authenticate its group membership.

The encrypted envelope and the ZK proof are dispatched to the relay server under the target group_id.

The server validates the proof and fans the envelope out to the active distribution list linked to that blinded group context.

### 8.3.2 Inbound Receive Path

The application reads the incoming group_id and looks up the corresponding local conversation record.

The client fetches the target GroupMasterKey, decrypts it, and spins up the ephemeral GroupSecretParams.

The client uses the rehydrated blob_key to decrypt the ciphertext envelope and extract the clear text payload or media attachment references.

The decrypted plaintext is written to the safe application read model, and the ephemeral native handles are securely zeroized from memory.

## 8.4 Membership Changes & Administrative State

Because group configuration (rosters, permissions, and roles) is highly sensitive, changes to group membership are not written directly to a public server database. Administrative operations are managed as state changes to an encrypted config file stored on the server, which can only be modified by producing an administrative zero-knowledge credential.

### 8.4.1 Adding a Member

An admin rehydrates the GroupSecretParams and decrypts the current member list using the blob_key.

The admin appends the new member's public key material, encrypts the updated roster with the blob_key, and issues a specialized ZK credential to the new member via a direct 1:1 session.

The admin uploads the newly encrypted configuration blob to the server using an administrative ZK proof.

### 8.4.2 Removing a Member (Cryptographic Eviction)

In legacy protocols, removing a user required forced key rotations across every remaining participant. Under Group V2, eviction is handled by a direct epoch update to the root secret:

The admin generates a brand new GroupMasterKey for the next epoch of the conversation.

The admin derives the new GroupSecretParams and blob_key from this fresh root secret.

The admin encrypts the updated membership roster (which now completely excludes the evicted peer) using the brand new blob_key.

The admin distributes the new GroupMasterKey directly to each remaining participant individually via their secure 1:1 Double Ratchet sessions.

The admin pushes the new group configuration epoch to the server.

**Epoch Cutover Race Condition Constraint:**

During the eviction process, the outbound message queue must block any concurrent sends to the old epoch while the 1:1 Double Ratchet tunnels are transmitting the new GroupMasterKey to the remaining members. If a message is sent signed with the old epoch parameters during this window:

- The server's ZK verification layer may reject the proof if it enforces strict epoch numbers.
- The evicted user may still be able to read the message if they intercept the packet before the epoch cutover is finalized on the relay.

The implementation must ensure that epoch transitions are atomic with respect to outbound message delivery to prevent these scenarios.

Because the evicted user is excluded from the 1:1 key distribution phase, they never receive the new GroupMasterKey. They can no longer rehydrate the current blob_key, cannot decrypt future message envelopes, and cannot mathematically construct a valid Zero-Knowledge presentation proof to access the server's routing endpoints. They are permanently locked out of the cryptographic context of the conversation.

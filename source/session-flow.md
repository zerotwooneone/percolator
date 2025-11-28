 # Signal session flows: models, persistence, security

 This document describes Signal/X3DH bootstrapping and Double Ratchet session flows, focusing on:
 - Protobuf wire models
 - Client-side persistence (SQLite-like) for pending and established sessions
 - Security and privacy considerations

 Sections
 - Part 1: Standard Flow (Alice initiates; Bob may be offline)
 - Part 2: Reverse-Signal Flow (Alice invites Bob to initiate)
 - Part 3: Ongoing Session Persistence

 ---

 ## Part 1: Standard Flow (Alice Initiates with Offline Bob)

 Alice sends a pre-key message, then finalizes the session after Bob’s first ratchet message.

 ### Step 1.1: Fetch Pre-Key Bundle and Persist Pending Session

 Alice fetches Bob’s bundle and derives the Initial Root Key (IRK). Persist a short‑lived pending record.

 ```protobuf
 // Alice asks the server for Bob's keys
 message GetPreKeyBundleRequest {
   string remote_user_id = 1;
 }

 // The server returns Bob's public keys
 message GetPreKeyBundleResponse {
   bytes remote_identity_key = 1;
   bytes remote_signed_pre_key = 2;
   bytes pre_key_signature = 3;
   optional bytes remote_one_time_pre_key = 4;
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

 ## Part 2: Reverse-Signal Flow (Alice Invites Bob to Initiate)

 Alice invites Bob to initiate a session with her. This can be via a relay/host.

 Note
 - Less private than Standard Flow (Alice reveals bundle first). Consider product UX tradeoffs.

 ### Step 2.1: Alice Sends Invitation

 ```protobuf
 // Opaque to the transport, but this is the inner invitation content
 message InviteHandshakeRequest {
   string local_invitation_id = 1; // Alice's correlation id

   // Alice's Pre-Key Bundle
   bytes alice_identity_key     = 2;
   bytes alice_signed_pre_key   = 3;
   bytes pre_key_signature      = 4;
   optional bytes alice_one_time_pre_key = 5;
 }
 ```

 SQLite (Alice, SentInvitations)

 | Column                     | Type     | Description                                                       |
 | -------------------------- | -------- | ----------------------------------------------------------------- |
 | local_invitation_id        | VARCHAR  | Primary Key. Alice-generated id for correlation.                  |
 | target_user_id             | VARCHAR  | Who the invite is for (e.g., Bob).                                |
 | alice_one_time_private_key | BLOB     | Only if an OTK was published; encrypted at rest; short‑lived.     |
 | created_at                 | DATETIME | For cleanup.                                                      |

 Security
 - Only persist a one-time key private part if required by implementation; encrypt at rest and purge on response.

 ### Step 2.2: Bob Queues for Approval

 Bob receives the invite and awaits explicit consent.

 SQLite (Bob, PendingInvitations)

 | Column               | Type     | Description                                  |
 | -------------------- | -------- | -------------------------------------------- |
 | invitation_id        | VARCHAR  | From `local_invitation_id`.                  |
 | inviter_identity_key | BLOB     | Alice’s identity key (for UI/verification).  |
 | invitation_blob      | BLOB     | Serialized invite payload.                   |
 | status               | TEXT     | e.g., “AwaitingUserApproval”.                |

 ### Step 2.3: Bob Accepts and Responds

 ```protobuf
 message InviteHandshakeResponse {
   string local_invitation_id = 1;  // Echo back to Alice

   // Bob acts as X3DH initiator now
   bytes bob_identity_key       = 2;
   bytes bob_x3dh_ephemeral_key = 3;

   // First ratchet message (header has Bob's next ratchet key)
   bytes initial_ratchet_message = 4;
 }
 ```

 - Bob establishes his side immediately; persists full session (see Part 3).
 - Bob deletes `PendingInvitations` record.

 ### Step 2.4: Alice Finalizes

 - Lookup `SentInvitations` by `local_invitation_id`.
 - Complete X3DH on Alice’s side; initialize double ratchet; decrypt `initial_ratchet_message`; retrieve `session_id`.
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

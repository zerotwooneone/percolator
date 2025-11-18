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
 - Slow-path: Iterate `PendingSessions`. For each:
   - Recompute the expected root from stored IRK and the header’s `public_ratchet_key`.
   - The record that yields a valid initialize/decrypt is the match.
 - Extract `session_id` from the decrypted inner payload.
 - Create a full session record (see Part 3), then delete the pending record.

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

# Signal Group Messaging: Models, Persistence, and Security

This document describes the **Sender Key** protocol used for efficient group messaging within the Signal ecosystem (Private Groups). Unlike 1:1 sessions which use the Double Ratchet, group messaging uses a centralized symmetric-key ratchet per member to ensure efficiency.

## Core Concept: Sender Keys

In a standard Double Ratchet (1:1), every message triggers a key exchange or state update on both sides. In a group of 50 people, doing this pairwise ($50 \times 49$ connections) is inefficient.

Instead, Signal groups use **Sender Keys**:
1.  **Alice** generates a random 32-byte **Chain Key** and a **Signing Keypair**.
2.  Alice encrypts this "Sender Key Bundle" and sends it to Bob, Charlie, and David using her existing 1:1 Double Ratchet sessions.
3.  When Alice sends a message to the group, she derives a key from her Chain Key, encrypts the message **once**, and sends that same ciphertext to everyone.
4.  Bob, Charlie, and David use their copy of Alice's Chain Key to derive the same decryption key.

---

## Part 1: Domain Models & Protobufs

### 1.1 The Sender Key Distribution Message
This message is sent **1:1** (inside an existing Double Ratchet session) when a member joins a group, or when keys are rotated.

```protobuf
// Sent inside a 1:1 encrypted tunnel
message SenderKeyDistributionMessage {
  bytes group_id = 1;
  uint32 id = 2;              // Distribution ID
  uint32 iteration = 3;       // Current chain iteration
  bytes chain_key = 4;        // The seed for the message chain
  bytes signing_key = 5;      // The public key used to sign messages
}
```

### 1.2 The Group Ciphertext Message
This is the actual chat message sent to the group. It is encrypted using AES-CBC (or GCM) with a key derived from the Sender Key chain.

```protobuf
message SenderKeyMessage {
  uint32 id = 1;              // Distribution ID
  uint32 iteration = 2;       // The ratchet step (sequence number)
  bytes ciphertext = 3;       // The encrypted content
  bytes signature = 4;        // Signed by the Sender's Signing Key
}
```

---

## Part 2: Persistence (SQLite)

The client must persist the cryptographic state for *every* member of the group.

### Table: `sender_keys`
Stores the receiving state for other members, and the sending state for ourselves.

| Column | Type | Description |
| :--- | :--- | :--- |
| `group_id` | BLOB | Composite Primary Key. |
| `sender_id` | VARCHAR | Composite Primary Key. The member who owns this key. |
| `device_id` | INTEGER | Signal allows multi-device; each device has its own chain. |
| `distribution_id` | INTEGER | Unique ID for this specific key generation. |
| `chain_key` | BLOB | The current 32-byte Chain Key. |
| `signing_key_public`| BLOB | The public key used to verify signatures from this sender. |
| `signing_key_private`| BLOB | (Self only) Private key for signing own messages. |
| `iteration` | INTEGER | Current message counter. |

---

## Part 3: Sending a Group Message

When Alice wants to send a message to the group:

### 3.1. Load State
Alice loads her own record from the `sender_keys` table for this `group_id`.

### 3.2. Symmetric Ratchet (KDF)
She uses her current `chain_key` to derive a **Message Key**.
1.  **Input:** Current Chain Key.
2.  **HMAC-SHA256:**
    * $MessageKey = HMAC(ChainKey, "0x01")$
    * $NextChainKey = HMAC(ChainKey, "0x02")$

### 3.3. Encryption & Signing
1.  **Encrypt:** Alice encrypts the payload using the $MessageKey$ (and a random IV).
2.  **Sign:** Alice signs the ciphertext using her `signing_key_private`.
3.  **Construct:** She creates a `SenderKeyMessage` with the `ciphertext`, `iteration`, and `signature`.

### 3.4. Persistence & Transport
1.  **Update DB:** She overwrites her `chain_key` with $NextChainKey$ and increments `iteration`.
2.  **Transport:** She sends the `SenderKeyMessage` to the server. The server fans this out to Bob, Charlie, and David.

---

## Part 4: Receiving a Group Message

Bob receives a `SenderKeyMessage` from Alice.

### 4.1. Lookup
Bob extracts the `sender_id` (from the transport envelope) and the `distribution_id` (from the message). He queries `sender_keys` to find Alice's key record.

### 4.2. Ratchet Forward (Fast-Forward)
If the message `iteration` is higher than Bob's stored `iteration`:
1.  Bob runs the KDF (Step 3.2) repeatedly to advance the chain until it matches the message's iteration.
2.  **Key Storage:** Intermediate message keys are stored/cached to decrypt out-of-order messages if they arrive later.
3.  **Update DB:** Bob updates the stored `chain_key` and `iteration`.

### 4.3. Decrypt & Verify
1.  Bob uses the derived $MessageKey$ to decrypt the `ciphertext`.
2.  Bob verifies the `signature` using Alice's stored `signing_key_public`.
3.  If valid, the message is displayed.

---

## Part 5: Group Administration (Private Groups)

The server is **Zero-Knowledge**. It does not know the group name or member list. All changes are managed via signed **Group Update Messages**.

### 5.1. The Master Key
* **Role:** Encrypts group metadata (Title, Avatar, Topic).
* **Distribution:** Shared via 1:1 channels (Double Ratchet) during an invite.
* **Server View:** The server sees an encrypted blob for the group title. It cannot read it.

### 5.2. Adding a Member (Alice adds David)
1.  **Update State:** Alice updates her local `PrivateGroup` aggregate to include David.
2.  **Command:** Alice creates a `GroupUpdate` protobuf (Action: ADD, Target: David).
3.  **Sign:** Alice signs this update with her Identity Key.
4.  **Send:** Alice sends this blob to Bob, Charlie, and David via 1:1 channels.
5.  **Sender Key Distribution:** Alice MUST send her current `SenderKeyDistributionMessage` to David so he can read her future messages.

### 5.3. Removing a Member (Alice removes Bob)
This requires a **Sender Key Rotation** to enforce forward secrecy and revoke access.

1.  **Command:** Alice sends a signed `GroupUpdate` (Action: REMOVE, Target: Bob) to Charlie and David.
2.  **Rotation:** Alice knows Bob has her current Chain Key. She must abandon it.
3.  **Generate New:** Alice generates a fresh Chain Key and Signing Keypair.
4.  **Distribute:** Alice sends a `SenderKeyDistributionMessage` containing the *new* key to Charlie and David (via 1:1 channels). She **does not** send it to Bob.
5.  **Result:** When Alice sends the next group message, she uses the new chain. Bob cannot decrypt it.

### 5.4. Handling Collisions
Since administration is decentralized, two admins might add a user at the same time. Signal uses a **logical clock** or **sequence number** stored in the `groups` table.
* Updates must include the previous `sequence_number`.
* If a client receives an update with a gap in the sequence, it requests the missing history from peers before applying the new state.
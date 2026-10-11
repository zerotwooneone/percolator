# Percolator Chat Application Specification

## 1. Overview & Scope

The Chat Application (`AppId: 0x01`) is the core messaging subsystem within the Percolator network. It provides end-to-end encrypted (E2EE) communications across two distinct channel topologies:
1. **1:1 Pairwise Channels**: Direct peer-to-peer or store-and-forward relayed channels secured via Double Ratchet / X3DH sessions.
2. **Multi-Party Group Channels**: Distributed groups secured via Sender Keys and epoch-managed Zero-Knowledge group ledgers.

### 1.1 Non-Goals & Architectural Boundaries
- **Network & Cryptographic Transport**: The Chat Application does not implement cryptographic handshakes, Diffie-Hellman operations, or transport protocols. It operates strictly as an application payload consumer and producer on top of established channels.
- **Out-of-Band Large File Swarms**: Bulk multi-peer file transfers and BitTorrent-style swarms are governed exclusively by the File Transfer subsystem (`AppId: 0x03`). Chat handles inline media and conversational attachments ($\le 50\text{ MB}$) only.
- **Rendezvous & DHT Operations**: Peer discovery and DHT routing pings are isolated to the Discovery subsystem (`AppId: 0x02`).

---

## 2. Privacy & Threat Model

The Chat Application is designed under a **Zero-Knowledge / Zero-Metadata** threat model matching or exceeding the Signal Protocol:

### 2.1 Sealed Sender & Relay Isolation
- **Outer Relay Stealth**: Outer transport envelopes visible to network relays carry **zero sender identity** and **zero channel identifiers**. A relay observes only a recipient blinded routing token and an unauthenticated delivery token.
- **Inner Payload Privacy**: Sender identities, channel identifiers, and conversational metadata reside strictly within the decrypted inner payload. A compromised or hostile relay cannot construct social graphs or link conversations.

### 2.2 Traffic Analysis Resistance (Stepped Bucket Padding)
- Because symmetric authenticated encryption (AEAD) does not obscure plaintext length, raw message lengths leak linguistic entropy, language structure, and interaction types (e.g. distinguishing a 1-character reaction from a paragraph of text).
- All chat frames over the wire mandate pseudo-random padding expanding the inner serialized payload to discrete stepped boundaries:
  - Tier 1: $\le 256\text{ bytes}$
  - Tier 2: $\le 512\text{ bytes}$
  - Tier 3: $\le 1024\text{ bytes}$
  - Tier 4: $\le 2048\text{ bytes}$
  - Tier 5: $\le 4096\text{ bytes}$
  - Increments of $4096\text{ bytes}$ thereafter.

### 2.3 Cryptographic Deniability & Anti-Tombstone Leakage
- **No Plaintext Quote Snippets**: When replying to a message, wire payloads reference parent messages exclusively by `(ParentMessageId, ParentAuthorId)`. Verbatim text snippets are **never** transmitted on the wire.
- **Tombstone Integrity**: If a user retracts a message via a tombstone, subsequent or concurrent replies cannot resurrect or permanently preserve the retracted plaintext. Receiving clients resolve reply quotes strictly from their local message store.

### 2.4 Clock Fingerprinting Defense
- Sub-millisecond timestamps leak local operating system hardware clock ticks, NTP synchronization jitter, and machine-specific drift, enabling cross-session identity correlation.
- All timestamps transmitted on the wire are clamped to whole Unix milliseconds, discarding sub-millisecond clock fractions.

### 2.5 Mention Span Invariant
- Mentions are represented as `(IdentityId, StartOffset, Length)` measured strictly in **UTF-8 byte offsets**.
- Wire formats never transmit recipient display names or contact nicknames. The recipient renders mentions using their own locally configured contact alias, preventing display name spoofing and Unicode surrogate pair slicing bugs.

### 2.6 Media Metadata Scrubbing
- Camera, microphone, and OS-generated metadata (EXIF tags, GPS coordinates, device serial numbers, camera model strings) are scrubbed client-side prior to encryption.
- Default photo/video filenames (e.g., `IMG_20261010_180512.jpg`) are stripped before transmission and replaced with generic media types or omitted entirely. Only explicitly designated document file attachments retain sanitized filenames.

---

## 3. Data & Wire Contract Semantics

All chat traffic over the wire is packaged in a two-tier structure: an outer typed container with an explicit discriminator and stepped padding, wrapping a specific message payload.

### 3.1 Payload Types & Wire Discriminator
Every chat frame is identified by a 1-byte discriminator:
1. `Message` ($0x01$): Text messages, rich text, mentions, replies, link previews, and media descriptors.
2. `Edit` ($0x02$): Retroactive content updates to an existing message.
3. `Tombstone` ($0x03$): Remote deletion/retraction of an existing message.
4. `Reaction` ($0x04$): Emoji reaction additions or removals.
5. `ReadReceipt` ($0x05$): Delivery/read marker advancing the remote read cursor.
6. `SystemNotice` ($0x06$): Protocol and administrative announcements (epoch change, membership updates).
7. `GroupInvitation` ($0x07$): Inbound invitation to join a group channel.
8. `GroupInvitationResponse` ($0x08$): Acceptance or decline of an invitation.

### 3.2 Rich Text & Markdown Support
Message content supports a deterministic subset of GitHub Flavored Markdown:
- Bold (`**text**`), Italic (`*text*`), Strikethrough (`~~text~~`)
- Inline code (`` `code` ``) and code blocks (```` ```lang ````)
- Blockquotes (`> quote`)

### 3.3 Link Previews
- **Zero Recipient Deanonymization**: Recipients **never** fetch OpenGraph tags or URL previews from the internet. Doing so leaks the recipient's IP address and browsing habits to web servers.
- **Sender-Generated Model**: The sender optionally generates preview metadata (`Title`, `Description`, `CanonicalUrl`) and a small encrypted thumbnail blob, embedding them directly within the E2EE frame.

---

## 4. Message Lifecycles & State Transitions

### 4.1 Message Edits
- **Window**: Allowed within 24 hours of the original message dispatch timestamp. Edits past 24 hours are rejected by receivers.
- **Authorization**: Only the original author may edit a message.
- **Auditability**: Receiving clients update the existing message record, preserve the original creation timestamp, and mark the message as `(edited)` with the edit timestamp.

### 4.2 Message Deletions (Tombstones)
- **Window**: Allowed within 24 hours of original send timestamp.
- **Authorization**: The original author may delete their message. Group channel administrators may delete messages from any participant if channel policy permits.
- **Tombstone Semantics**: Replaces the message content with a deleted placeholder (*"This message was deleted"*), preserves thread position and reply linkage, and triggers permanent deletion of local cached media blobs.

### 4.3 Emoji Reactions
- **Composite Key**: Identified by `(MessageId, ReactorId, Emoji)`.
- **Idempotency**: Repeated add requests for the same emoji by the same user produce no duplicate entries; repeated remove requests succeed silently.
- **Projection**: Read queries expose aggregated reaction counts, participant lists, and whether the local user has reacted.

### 4.4 Disappearing Messages
- **Configuration**: Configurable per channel or overridden per message with a time-to-live (`ExpireAfterSeconds`).
- **Sender Lifecycle**: Timer starts counting down immediately upon message dispatch (`ExpiresAt = DispatchTime + TTL`).
- **Recipient Lifecycle**:
  - 1:1 Channels: Timer starts counting down when the recipient displays the message in the viewport or marks it read.
  - Group Channels: Timer starts upon local receipt and read cursor progression.
- **Purge Enforcement**:
  - Background Worker: A periodic scheduled worker sweeps the local database, deletes expired message rows, and purges associated media files from disk.
  - Query Filter: All message queries filter out expired records regardless of whether the periodic background sweep has fired.

### 4.5 Group Invitation Consent Protocol
- **Transport**: Inbound invitations to join a group channel travel exclusively over an established **1:1 Pairwise Channel** between the inviter and invitee. This ensures invitations are end-to-end encrypted and spam-protected by existing pairwise trust.
- **Consent State Machine**:
  - States: `Pending`, `Accepted`, `Declined`, `Expired`.
  - Inbound invitations require explicit user approval.
  - On Acceptance: The local client joins the group channel, dispatches an acceptance response to the inviter over the 1:1 channel, and initializes Sender Key distribution.
  - On Decline: The local client marks the invitation declined, notifies the inviter, and suppresses further uninvited group traffic.

---

## 5. Media Attachment Architecture

Chat balances low-latency message delivery against the Double Ratchet's 64 KB per-message payload limit:

### 5.1 Inline vs. Out-of-Band Media
- **Inline Media ($\le 32\text{ KB}$)**: Voice clips, animated stickers, custom emojis, and BlurHash image previews are encrypted and embedded directly within the message payload. Zero extra network roundtrips.
- **Out-of-Band Media ($32\text{ KB}$ to $50\text{ MB}$)**: High-resolution photos, audio files, and video clips are encrypted using chunked STREAM AEAD (64 KB chunks) and uploaded out-of-band. The chat message carries only a compact `BlobReference` (keys, nonces, SHA-256 digest, dimensions, unpadded size).

### 5.2 Channel Topology Expectations
1. **1:1 Direct Channel (No Relay)**:
   - Media transfer requires **both peers to be online concurrently**.
   - Sender stages encrypted chunks in local disk storage. Recipient pulls chunks directly from sender over an authenticated peer data stream.
2. **1:1 Relayed Channel**:
   - Sender uploads encrypted blob to designated channel relay.
   - Recipient downloads asynchronously from relay upon coming online.
3. **Group Channel**:
   - Sender uploads encrypted blob **exactly once** to the group's common relay.
   - All group members download chunks from that common relay using the keys distributed inside the Sender Key ratchet message.

---

## 6. CQRS & Architectural Invariants

### 6.1 Domain Channel Ownership (Write Authority)
- `DirectChannel` and `GroupChannel` domain aggregates are the **sole authoritative owners** of channel membership, roles, cryptographic epochs, and raw payload sequencing.
- Every chat message, edit, reaction, and receipt appends an immutable payload record to the domain channel aggregate to maintain an audit trail and preserve replay integrity.

### 6.2 Fast-Path Read Projections
- User interfaces and query handlers do **not** hydrate heavy domain channel aggregates.
- Read operations execute optimized, indexed queries against projection stores, retrieving paged timelines, conversation summaries, and unread badge counts with sub-millisecond latency.

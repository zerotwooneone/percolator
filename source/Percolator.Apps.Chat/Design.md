# Percolator.Apps.Chat

Spec: [design-spec](../spec/design-spec.md)

## 1. Purpose and Layer Boundary

`Percolator.Apps.Chat` is the conversational messaging plugin (`AppId: 0x01`) built atop [`Percolator.PluginSdk`](../Percolator.PluginSdk/Design.md) and [`Percolator.Domain`](../Percolator.Domain/Design.md). It orchestrates 1:1 and group messaging lifecycles, edits, tombstones, reactions, disappearing message policies, group invitation consent, and conversational media attachments.

- **In Scope:** Chat payload lifecycles, traffic-analysis padding rules, metadata/filename scrubbing, disappearing message expiration rules, pairwise 1:1 group invitation consent state machines, and CQRS timeline read query ports.
- **Out of Scope:** Underlying ratchet cryptography and outbox delivery (`Percolator.Domain` / `Percolator.Application`), large multi-peer file swarms (`Percolator.Apps.FileTransfer`), and concrete Protobuf/SQLite/AEAD adapters (`Percolator.Infrastructure`).

---

## 2. Architectural Invariants and Encapsulation

1. **Domain Channel Write Authority vs. Fast-Path Timeline Queries:** `DirectChannel` and `GroupChannel` aggregates in [`Percolator.Domain`](../Percolator.Domain/Design.md) are the sole authoritative write owners of channel membership, roles, epochs, and payload sequencing; UI timeline paging (`IChatTimelineQueryService`) queries indexed read projections directly without hydrating domain channel aggregates.
2. **Cross-Channel & Sender Authorization Guard:** Inbound chat frames verify that the authenticated envelope sender matches the claimed message author and belongs to the target channel before any state mutation or projection update occurs.
3. **Dual-Enforcement Disappearing Messages:** Expired messages are excluded immediately at query time by read-projection filters in addition to asynchronous background database/blob sweeping, ensuring expired content is never returned even between sweep intervals.

---

## 3. Key Technical Design Choices

1. **Stepped Bucket Padding Prior to Encryption:**
   - *Design Choice:* Serialized chat frames are padded to discrete stepped size tiers before encryption via `IChatWireSerializer`, preventing passive observers or relays from inferring interaction types (such as a single-emoji reaction vs. a text reply) from ciphertext length.
   - *Rejected Alternative:* Encrypting unpadded variable-length serialized payloads, which leaks linguistic and interaction entropy through AEAD length preservation.
2. **Zero-Plaintext Reply References (Anti-Tombstone Leakage):**
   - *Design Choice:* Wire reply structures transmit only `(ParentMessageId, ParentAuthorId)` without quoted text snippets; receiving clients resolve reply previews from their local database.
   - *Rejected Alternative:* Embedding quoted plaintext inside reply payloads, which permanently preserves retracted content on remote devices even after the original message is tombstoned or expires.
3. **Sender-Generated Link Previews Only:**
   - Receiving clients never fetch OpenGraph metadata or URLs from the internet upon receiving a message (which would leak the recipient's IP address and reading timing to external web servers); link previews and encrypted thumbnails are generated exclusively by the sender.
4. **Pairwise 1:1 Transport for Group Invitations:**
   - Group invitations travel exclusively over an established 1:1 channel between inviter and invitee and require explicit local user consent (`Pending` $\rightarrow$ `Accepted`/`Declined`) before the client joins the group or initializes Sender Keys.

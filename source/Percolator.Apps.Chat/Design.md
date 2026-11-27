# Percolator.Apps.Chat

Spec: [design-spec](../spec/design-spec.md)

## 1. Purpose and Layer Boundary

`Percolator.Apps.Chat` is the conversational messaging plugin (`AppId: 0x01`) built atop [`Percolator.PluginSdk`](../Percolator.PluginSdk/Design.md) and [`Percolator.Domain`](../Percolator.Domain/Design.md). It orchestrates 1:1 and group messaging lifecycles, edits, tombstones, reactions, disappearing message policies, group invitation consent, and conversational media attachments.

- **In Scope:** Chat payload lifecycles, traffic-analysis padding rules, metadata/filename scrubbing, disappearing message expiration rules, pairwise 1:1 group invitation consent state machines, and CQRS timeline read query ports.
- **Out of Scope:** Underlying ratchet cryptography and outbox delivery (`Percolator.Domain` / `Percolator.Application`), large multi-peer file swarms (`Percolator.Apps.FileTransfer`), and concrete Protobuf/SQLite/AEAD adapters (`Percolator.Infrastructure`).

---

## 2. Architectural Invariants and Encapsulation

1. **`INV-CHAT-CQRS-AUTHORITY` (Domain Channel Write Authority vs. Fast-Path Timeline Queries):** `DirectChannel` and `GroupChannel` aggregates in [`Percolator.Domain`](../Percolator.Domain/Design.md) are the sole authoritative write owners of channel membership, roles, epochs, and payload sequencing; UI timeline paging (`IChatTimelineQueryService`) queries indexed read projections directly without hydrating domain channel aggregates. `#cqrs-projection`
2. **`INV-CHAT-SENDER-AUTH` (Cross-Channel & Sender Authorization Guard):** Inbound chat frames verify that the authenticated envelope sender matches the claimed message author and belongs to the target channel before any state mutation or projection update occurs.
3. **`INV-DUAL-EXPIRATION-GUARD` (Dual-Enforcement Disappearing Messages):** Expired messages are excluded immediately at query time by read-projection filters in addition to asynchronous background database/blob sweeping, ensuring expired content is never returned even between sweep intervals. `#ephemeral-retention`
4. **`INV-CHAT-INTRO-CONSENT-FLOW` (Ask-Before-Introducing Consent Invariant):** Peer introductions require two-phase local user consent: (1) the introducing peer must request and obtain explicit per-introduction consent (`IntroductionConsentResponse.Allow`) from the target contact over their existing 1:1 Double Ratchet channel before transmitting any `PeerIntroductionCard` to the recipient, and (2) the receiving peer stages the forwarded invitation in pending-consent status ([`SYS-CONTACT-CONSENT-GATE`](../SystemRequirements.md#1-functional-system-capabilities)), ensuring zero unsolicited contact activation or identity disclosure without mutual user consent. `#relay-privacy` `#introductions`

---

## 3. Key Technical Design Choices

1. **`DEC-BUCKET-PADDING` (Stepped Bucket Padding Prior to Encryption):**
   - *Design Choice:* Serialized chat frames are padded to discrete stepped size tiers before encryption via `IChatWireSerializer`, preventing passive observers or relays from inferring interaction types (such as a single-emoji reaction vs. a text reply) from ciphertext length. `#relay-privacy`
   - *Rejected Alternative:* Encrypting unpadded variable-length serialized payloads, which leaks linguistic and interaction entropy through AEAD length preservation.
2. **`DEC-ID-ONLY-REPLIES` (Zero-Plaintext Reply References — Anti-Tombstone Leakage):**
   - *Design Choice:* Wire reply structures transmit only `(ParentMessageId, ParentAuthorId)` without quoted text snippets; receiving clients resolve reply previews from their local database. `#ephemeral-retention`
   - *Rejected Alternative:* Embedding quoted plaintext inside reply payloads, which permanently preserves retracted content on remote devices even after the original message is tombstoned or expires.
3. **`DEC-SENDER-LINK-PREVIEWS` (Sender-Generated Link Previews Only):**
   - Receiving clients never fetch OpenGraph metadata or URLs from the internet upon receiving a message (which would leak the recipient's IP address and reading timing to external web servers); link previews and encrypted thumbnails are generated exclusively by the sender.
4. **`DEC-PAIRWISE-GROUP-INVITES` (Pairwise 1:1 Transport for Group Invitations):**
   - Group invitations travel exclusively over an established 1:1 channel between inviter and invitee and require explicit local user consent (`Pending` $\rightarrow$ `Accepted`/`Declined`) before the client joins the group or initializes Sender Keys. `#group-membership`
5. **`DEC-ASK-BEFORE-INTRODUCING` (Ask-Before-Introducing Option A — Zero Profile Bloat, Zero New Crypto):**
   - *Design Choice:* Peer introductions are mediated inside `Percolator.Apps.Chat` using standard pairwise 1:1 Double Ratchet messages (`IntroductionConsentRequest`, `IntroductionConsentResponse`, and `PeerIntroductionCard`). [`ProfileMetadata`](../Percolator.PluginSdk/Profiles/IProfilePorts.cs) remains strictly identity presentation (`DisplayName`, `Avatar`, `StatusBio`, `Revision`) with zero invitation or routing bloat. Forwarded introduction cards contain standard [`PeerInvitation`](../Percolator.Domain/Identities/ValueObjects/PeerInvitation.cs) material (`InviterIdentityId`, `InviterIdentityKey`, `RelayEndpoint`, `RoutingToken`) generated on-demand by the consenting contact. `#relay-privacy` `#introductions`
   - *Rejected Alternative:* Auto-forwarding contact cards without consent (leaks social graphs and violates privacy) or embedding invitations into public/relayed profiles (bloats profiles, creates correlation vectors, and leaks pre-keys).

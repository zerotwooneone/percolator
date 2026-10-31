# Percolator.Apps.Discovery

Spec: [design-spec](../spec/design-spec.md)

## 1. Purpose and Layer Boundary

`Percolator.Apps.Discovery` is the privacy-preserving peer discovery and blinded rendezvous plugin (`AppId: 0x02`) built atop [`Percolator.PluginSdk`](../Percolator.PluginSdk/Design.md) and [`Percolator.Domain`](../Percolator.Domain/Design.md). It enables nodes to discover opt-in peers on sovereign relays and execute zero-knowledge contact matching without centralized user directories, crawlable global DHTs, or LAN broadcasts.

- **In Scope:** Two-phase candidate discovery workflows, sovereign relay directory admission and visibility policies, time-ratcheted HMAC-SHA256 blinded rendezvous, and outbound discovery storage/serialization ports.
- **Out of Scope:** Global DHT routing tables, UDP multicast/broadcast scanning, continuous online/offline presence heartbeats, X3DH handshake execution, and concrete Protobuf/SQLite adapters.

---

## 2. Architectural Invariants and Encapsulation

1. **`INV-DISCOVERY-DUAL-OPTIN` (Sovereign Dual-Sided Opt-In):** A peer is never discoverable by default; discovery requires both the relay's policy (`Open` vs. `BlindedContactsOnly` vs. `Disabled`) and the individual peer's explicit visibility posture (`Discoverable` vs. `Hidden`).
2. **`INV-DISCOVERY-ADMISSION-GATE` (Hard Cryptographic Admission & Anti-Impersonation Gate):** A relay admits a registration into its directory if and only if the peer has an established mutual session with the relay, holds an active Signed Pre-Key in its `RelayPreKeyLedger`, and the registration's advertised identity matches the authenticated envelope sender.
3. **`INV-DISCOVERY-ROUTE-PRIVACY` (Route Privacy):** Wide-area discovery responses expose only relayed delivery routes, never raw peer IP addresses. `#relay-privacy`

---

## 3. Key Technical Design Choices

1. **`DEC-TWO-PHASE-DISCOVERY` (Two-Phase Discovery to Defeat One-Time Pre-Key Starvation):**
   - *Design Choice:* Discovery is split into Phase 1 (candidate queries returning only public identity and delivery route descriptors, consuming **zero** One-Time Pre-Keys) and Phase 2 (fetching a candidate's pre-key bundle and initiating an X3DH handshake strictly upon explicit user intent to connect).
   - *Rejected Alternative:* Returning pre-key bundles directly inside directory search results, which allows an attacker to scrape the directory and exhaust every listed peer's OPK pool.
2. **`DEC-BLINDED-RENDEZVOUS` (Time-Ratcheted Blinded Rendezvous Locators):**
   - Mutual contacts who share an out-of-band secret compute a daily UTC-ratcheted `HMAC-SHA256` blinded locator to deposit and query rendezvous descriptors on a common relay, enabling contact discovery with zero identity or social-graph disclosure to the relay. `#relay-privacy`

<!-- SPECIFICATION GOVERNANCE: Governed by Percolator.Desktop/spec/spec.spec.md and source/spec/deployable-requirements-spec.md -->

# Percolator.Desktop Requirements

Spec: [deployable-requirements-spec](../../spec/deployable-requirements-spec.md)

## 1. Segregation Role and Boundary Constraints

1. **`DEP-DESKTOP-SEGREGATION-ROLE` (Composition Root & Presentation Boundary):** `[Verify: StaticAnalysis]`
   `Percolator.Desktop` is isolated as the top-level user-facing presentation and composition-root deployable for the Percolator application. It is strictly forbidden from defining domain aggregate entities, executing direct unmanaged cryptographic operations, executing raw SQL commands, or bypassing the application layer command/query pipeline (IEC 62304 Clause 5.3.4). Furthermore, all presentation models and UI views MUST remain segregated from domain aggregate mutation methods, and unquarantined SOUP handles (`IntPtr`, `SafeHandle`) MUST NOT cross presentation boundaries.

---

## 2. Allocated Responsibilities

1. **`DEP-DESKTOP-PRE-ACTIVATION-STEALTH` (Pre-Activation Network Stealth Startup Gate):** `[Verify: Integration]`
   The deployable shall ensure that launching the application, executing bootstrap dependency injection, and unlocking the local database binds zero listening TCP/UDP network sockets, emits zero DHT announcements, and initiates zero STUN/TURN discovery probes until a user persona is explicitly selected and confirmed in the identity gate (realizes [`SYS-PRE-ACTIVATION-STEALTH`](../../SystemRequirements.md#2-security-privacy-and-risk-control-invariants)).

2. **`DEP-DESKTOP-STARTUP-VAULT-AUTHENTICATION` (Pre-Unlock Vault Security Gate):** `[Verify: Integration]`
   The deployable shall present a dedicated startup frame enforcing credential verification before instantiating or exposing any domain repositories, background sync workers, or relational database connection pools. Entering an established emergency duress passphrase shall trigger instantaneous cryptographic header shredding, generate a valid decoy vault, and transition to an authentic vault state persisting created personas with zero forensic failure indicators (realizes [`SYS-AT-REST-VAULT-ENCRYPTION`](../../SystemRequirements.md#2-security-privacy-and-risk-control-invariants) and [`SYS-DECOY-VAULT-DENIABILITY`](../../SystemRequirements.md#2-security-privacy-and-risk-control-invariants)).

3. **`DEP-DESKTOP-SESSION-LOCK-ZEROIZATION` (Session Lock Teardown and Memory Zeroization):** `[Verify: Unit]`
   The deployable shall execute a complete session teardown upon user-initiated lock or idle timeout—immediately closing all open network sockets, clearing active database connection pools, disposing the authenticated service container, zeroizing master key memory buffers via cryptographic zeroization, and returning the user interface to the pre-unlock authentication gate (realizes [`SYS-LOCK-TEARDOWN-AND-ZEROIZATION`](../../SystemRequirements.md#2-security-privacy-and-risk-control-invariants) and [`SYS-DETERMINISTIC-ZEROIZATION`](../../SystemRequirements.md#2-security-privacy-and-risk-control-invariants)).

4. **`DEP-DESKTOP-UNSOLICITED-CONSENT-GATE` (Inbound Unsolicited Connection Consent Staging):** `[Verify: Integration]`
   The deployable shall stage inbound connection requests and handshake offers from unknown cryptographic identities in a pending-consent view state without displaying intrusive operating system notifications, triggering audio chimes, or automatically establishing active conversations until the user explicitly reviews and accepts the contact (realizes [`SYS-CONTACT-CONSENT-GATE`](../../SystemRequirements.md#1-functional-system-capabilities)).

5. **`DEP-DESKTOP-PERSONA-ISOLATION` (Cryptographic Persona Context Segregation):** `[Verify: Unit]`
   The deployable shall maintain strict isolation between multiple cryptographic personas hosted in the same local vault, ensuring that desktop UI preferences, port reservations, and active session views associated with one persona do not cross-contaminate or leak telemetry to another persona (realizes [`SYS-ISOLATED-PERSONAS`](../../SystemRequirements.md#1-functional-system-capabilities)).

6. **`DEP-DESKTOP-OUT-OF-BAND-VERIFICATION` (Out-of-Band Contact Verification Presentation):** `[Verify: Integration]`
   The deployable shall present deterministic, human-comparable cryptographic verification fingerprints binding identity keys for established peer contacts, displaying explicit verification state badges in the user interface (realizes [`SYS-OUT-OF-BAND-VERIFICATION`](../../SystemRequirements.md#1-functional-system-capabilities)).

7. **`DEP-DESKTOP-KEY-CHANGE-WARNING` (Identity Key Change Suspension and Alert):** `[Verify: Integration]`
   The deployable shall intercept identity key rotation events for existing contacts, immediately suspending message transmission to that contact and presenting an un-dismissible confirmation dialog requiring explicit user acknowledgment before resuming communication (realizes [`SYS-KEY-CHANGE-TRUST-HALT`](../../SystemRequirements.md#2-security-privacy-and-risk-control-invariants)).

8. **`DEP-DESKTOP-MEDIA-METADATA-SANITIZATION-INGRESS` (Media File Ingress Defense):** `[Verify: Unit]`
   The deployable shall ensure that outbound image and media attachments selected through user interface file pickers are passed to sanitization ports to strip EXIF, GPS coordinates, and camera serial numbers before chunking and transmission (realizes [`SYS-MEDIA-METADATA-SANITIZATION`](../../SystemRequirements.md#2-security-privacy-and-risk-control-invariants)).

9. **`DEP-DESKTOP-VOLATILE-DRAFT-ISOLATION` (Volatile Drafts & Disappearing Message Presentation):** `[Verify: Unit]`
   The deployable shall preserve uncommitted message drafts exclusively in volatile memory across conversation and persona switches without persisting unencrypted draft text to non-volatile disk caches or relational stores, and shall render visual expiration countdown indicators on disappearing messages without retaining expired message content in presentation memory after purge (realizes [`SYS-RETENTION-PURGE`](../../SystemRequirements.md#1-functional-system-capabilities)).

10. **`DEP-DESKTOP-PEER-BLOCK-UI-SILENCE` (Peer Block UI Indistinguishability & Visual Cleansing):** `[Verify: Integration]`
    The deployable shall immediately suppress all visual badges, audio alerts, and desktop notifications originating from blocked identities, rendering the blocked peer indistinguishable from an offline contact with zero UI presence leakage (realizes [`SYS-BLOCK-WIRE-INDISTINGUISHABILITY`](../../SystemRequirements.md#2-security-privacy-and-risk-control-invariants)).

---

## 3. Public Contract Invariants

1. **`DEP-DESKTOP-TWO-STAGE-CONTAINER-LIFECYCLE` (Container Hierarchy and Isolation):** `[Verify: Unit]`
   The deployable shall partition application dependency injection into two discrete tiers: a lightweight root bootstrap container containing exclusively presentation coordinators and unlock infrastructure, and an ephemeral authenticated container child scope containing database contexts and domain services that is instantiated only after successful vault unlock and destroyed upon session lock (IEC 62304 Clause 5.3.2).

2. **`DEP-DESKTOP-PRESENTATION-MODEL-ISOLATION` (UI-Agnostic CQRS Read Model Boundary):** `[Verify: StaticAnalysis]`
   All user-facing views shall bind exclusively to presentation models and read-only DTO snapshots. Presentation components are strictly prohibited from mutating domain aggregate state directly or executing synchronous blocking queries on the UI thread (satisfies [`SYS-RESULT-DRIVEN-INGRESS`](../../SystemRequirements.md#3-external-interfaces-and-resource-boundaries)).

3. **`DEP-DESKTOP-UI-THREAD-MARSHALING` (Reactive Boundary and Thread Marshalling):** `[Verify: Unit]`
   High-frequency reactive event streams and background worker updates shall be filtered and debounced on worker threads and marshaled to the UI dispatcher only at the terminal presentation boundary, ensuring the main user interface thread is protected from frame rate degradation (IEC 62304 Clause 5.3.2).

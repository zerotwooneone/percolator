## Missing building blocks to model Signal and Reverse-Signal flows

- Handshake invitation parser/serializer
  - Parser: read Contracts.EstablishSessionRequest from `HandshakeInvitation` to extract IK_A, EK_A, SPK id, optional OPK id (Signal flow, responder side).
  - Serializer: build Contracts.EstablishSessionRequest bytes from inputs to produce `HandshakeInvitation` (Reverse-Signal, initiator invites responder).
- Key store surface (responder-side private keys)
  - `GetIdentityPrivateKey()`, `GetSignedPreKeyPrivate(id)`, `TryGetOneTimePreKeyPrivate(id)` and future OPK consumption method.
- X3DH integration facades
  - We have `IX3dhDeriver` for Initiator/Responder; need thin helpers that adapt parsed invitation + keystore → derivation inputs.
- First message construction policy
  - We have `IRatchetEngine` and `SessionRatchetMessage`; define AD/payload policy for the responder’s first message (start with empty AD and payload; encapsulate in helper).
- Initiator-side invitation builder (Reverse-Signal)
  - Build invite bytes (Contracts.EstablishSessionRequest) from initiator’s IK_A/EK_A and target SPK/OPK identifiers.
- Pending artifacts models and API seams (hosted outside crypto)
  - SentInvitations and PendingSessions repositories live in Application/Infrastructure; crypto needs only ports/contracts to operate without persistence coupling.
- Session finalization helpers (initiator slow-path)
  - Given stored IRK and responder’s first `RatchetMessage`, decrypt payload, extract `session_id`, and return initialized session state. Provide a small facade around existing ratchet/state primitives.
- One-Time PreKey lifecycle
  - Policy/port for consuming OPKs upon use (future work per note).

# Finish Cryptography: Large-TDD Chunks (Invitation ↔ Response, Signal & Reverse-Signal)

Design goal: fewer, larger, cohesive chunks that each produce a robust, test-driven slice of functionality. Code does not need to compile between chunks. Each chunk begins with tests (Red), then implementation (Green), then refactor.

---

## Chunk A: End-to-end Invitation Contracts and Parsers (Signal + Reverse-Signal)

- Tests (write first)
  - Parse success: `HandshakeInvitation.Value` (Contracts.EstablishSessionRequest) → IK_A, EK_A, SPK id, optional OPK id.
  - Parse failures: missing IK_A, missing EK_A, missing SPK id; malformed bytes.
  - Serialize success (Reverse-Signal): build Contracts.EstablishSessionRequest from provided IK_A/EK_A/SPK/OPK → `HandshakeInvitation` bytes round-trip via parser.
- Implementation
  - `HandshakeInvitationParser.Parse(HandshakeInvitation)` → `ParsedInvitation` DTO with strongly-typed keys (RatchetIdentityKey, RatchetEphemeralKey) and ids (string/StringVO if available).
  - `HandshakeInvitationBuilder.Build(IK_A, EK_A, spkId, opkId?)` → `HandshakeInvitation` (bytes of Contracts.EstablishSessionRequest).
  - Strict protobuf presence checks per guidelines; never log key material.

---

## Chunk B: KeyStore Port + X3DH Bridges

- Tests (write first)
  - Bridge selects correct private keys given SPK/OPK ids; OPK optional path.
  - Failure when SPK missing.
- Implementation
  - Extend `IKeyStore` with minimal responder-read API:
    - `PrivatePreKey GetIdentityPrivateKey()`
    - `PrivatePreKey GetSignedPreKeyPrivate(string signedPreKeyId)`
    - `PrivatePreKey? TryGetOneTimePreKeyPrivate(string oneTimePreKeyId)`
  - `X3dhResponderBridge` that adapts `ParsedInvitation + IKeyStore` → `IX3dhDeriver.DeriveResponder(...)` inputs.
  - (Leave OPK consumption as future work.)

---

## Chunk C: CryptoPrimitives + First-Message Policy (Responder)

- Tests (write first)
  - `ICryptoPrimitives.CreateHandshakeResponse` returns bytes that decode into a valid `SessionRatchetMessage` with non-empty header key and ciphertext.
  - OPK present/absent paths both succeed.
  - Malformed invitation and missing SPK throw.
- Implementation
  - `CryptoPrimitives : ICryptoPrimitives` with ctor `(IX3dhDeriver, IRatchetEngine)`.
  - Flow: Parse invitation → use `X3dhResponderBridge` to derive `InitialRootKey` → initialize responder `RatchetState` → `IRatchetEngine.Encrypt(Plaintext.Empty, AD.Empty, counter:0, prevLen:0)` → `SessionRatchetMessage.Create(...)` → wrap as `HandshakeResponseMessage`.
  - Encapsulate AD/payload policy in a helper to keep it stable.

---

## Chunk D: Reverse-Signal Initiator Toolkit + Invitation Builder

- Tests (write first)
  - Build invitation from initiator inputs; parser round-trips all fields.
  - Initiator-side helper calls `IX3dhDeriver.DeriveInitiator(...)` and prepares initial state; produces first `RatchetMessage` similarly to responder policy.
- Implementation
  - `ReverseSignalInitiator` utilities to construct an invitation using `HandshakeInvitationBuilder`.
  - Helper to derive initiator initial state via `IX3dhDeriver.DeriveInitiator` and produce the first outbound `SessionRatchetMessage` (empty payload/AD policy mirroring responder).

---

## Chunk E: Session Finalization Helpers (Initiator Slow-Path) + Integration Tests

- Tests (write first)
  - Given stored IRK and a responder `SessionRatchetMessage`, decrypt payload successfully; extract `session_id` (from inner envelope placeholder) and initialize full session state.
  - Negative tests: decryption fails for mismatched state.
  - PendingSession integration tests using real `HandshakeInvitation` bytes and stubbed ports verify `ApproveAndRespond` and `AutoRespond` end-to-end.
- Implementation
  - `InitiatorFinalizer` helper that: (a) takes IRK + header key from message, (b) constructs initial `RatchetState`, (c) decrypts, (d) returns session initialization artifacts.
  - Light adapters around existing `RatchetState`, `IRatchetEngine`, and `SessionRatchetMessage` to keep tests readable and stable.

---

## Notes & Security

- Keep parser strict: require the required fields in `EstablishSessionRequest`.
- Do not log key material.
- Future work: OPK consumption (once)
- Ensure protobuf presence checks use Has* or null checks per our protobuf guidelines.

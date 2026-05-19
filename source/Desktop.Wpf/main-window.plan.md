# Signal.Interop API Surface

This section documents the intended *public* API surface of this repository as implemented.

## C# (.NET 9) API (`Signal.Interop`)

Primary entrypoint:

- **`public static class SignalCrypto`**
  - **`public static int TestConnection()`**
    - Diagnostic call to verify native library loading and basic interop.

  - **`public static GroupSecretParamsSafeHandle GenerateGroupSecretParams(ReadOnlySpan<byte> randomness32)`**
    - Creates a new Groups V2 `GroupSecretParams` from exactly 32 bytes of randomness.
    - Throws `ArgumentException` if `randomness32.Length != 32`.

  - **`public static GroupMasterKeySafeHandle GetGroupMasterKey(GroupSecretParamsSafeHandle secretParams)`**
    - Extracts the `GroupMasterKey` from an existing `GroupSecretParams`.
    - Intended use: persisting group state across sessions.

  - **`public static GroupSecretParamsSafeHandle DeriveGroupSecretParams(GroupMasterKeySafeHandle masterKey)`**
    - Derives `GroupSecretParams` deterministically from a persisted `GroupMasterKey`.

  - **`public static void GetGroupId(GroupSecretParamsSafeHandle handle, Span<byte> outBuffer)`**
    - Writes exactly 32 bytes of the group identifier.
    - Allocation-free.
    - Throws `ArgumentException` if the destination buffer length is not exactly 32.

  - **`public static void GetBlobKey(GroupSecretParamsSafeHandle handle, Span<byte> outBuffer)`**
    - Writes exactly 32 bytes of the blob key.
    - Allocation-free.
    - Throws `ArgumentException` if the destination buffer length is not exactly 32.

  - **`public static byte[] SerializeGroupMasterKey(GroupMasterKeySafeHandle masterKey)`**
  - **`public static void SerializeGroupMasterKey(GroupMasterKeySafeHandle masterKey, Span<byte> buffer32)`**
    - Serializes a master key into exactly 32 bytes.
    - The `Span<byte>` overload is the allocation-free option.
    - Throws `ArgumentException` if the destination buffer length is not exactly 32.

  - **`public static GroupMasterKeySafeHandle DeserializeGroupMasterKey(ReadOnlySpan<byte> bytes32)`**
    - Reconstructs a master key handle from exactly 32 serialized bytes.
    - Throws `ArgumentException` if `bytes32.Length != 32`.
    - Throws `CryptographicException` if native deserialization fails.

SafeHandle types (opaque native ownership):

- **`public sealed class GroupSecretParamsSafeHandle : SafeHandle`**
- **`public sealed class GroupMasterKeySafeHandle : SafeHandle`**

### Usage notes (C#)

- **Ownership / lifetime**
  - Handles own native allocations.
  - Always dispose with `using` / `Dispose()` as soon as possible.
  - Finalizers exist as a backstop, but explicit disposal is the intended usage.

- **Persistence model**
  - Persist group state by storing **only** the 32-byte serialized `GroupMasterKey` in your encrypted local database.
  - On next launch:
    - `DeserializeGroupMasterKey(bytes32)`
    - `DeriveGroupSecretParams(masterKey)`

- **Threading**
  - The wrapper uses `DangerousAddRef`/`DangerousRelease` when passing handles to native code to prevent races with finalization.
  - Treat handle instances as normal reference types; avoid disposing while concurrently using them.

## Rust C-ABI exports (`signal_shim`)

These functions are exported from the native library and are consumed by the C# wrapper. They are not intended to be called directly from application code.

- **Diagnostics**
  - `int32 signal_shim_test_connection()`

- **Allocation / freeing (must be paired)**
  - `void signal_zkgroup_group_secret_params_free(void* secret_params)`
  - `void signal_zkgroup_group_master_key_free(void* master_key)`
    - Both perform secure wiping before freeing.

- **Groups V2 primitives**
  - `int32 signal_zkgroup_group_secret_params_generate(const uint8_t* randomness32, size_t randomness_len, void** out_secret_params)`
  - `int32 signal_zkgroup_group_secret_params_get_master_key(const void* secret_params, void** out_master_key)`
  - `int32 signal_zkgroup_group_secret_params_derive_from_master_key(const void* master_key, void** out_secret_params)`
  - `int32 signal_zkgroup_group_secret_params_get_group_id(const void* secret_params, uint8_t* out_buffer, size_t buffer_len)`
    - Requires `buffer_len == 32`.
  - `int32 signal_zkgroup_group_secret_params_get_blob_key(const void* secret_params, uint8_t* out_buffer, size_t buffer_len)`
    - Requires `buffer_len == 32`.

- **GroupMasterKey persistence**
  - `int32 signal_zkgroup_group_master_key_serialize(const void* master_key, uint8_t* out_buffer, size_t buffer_len)`
    - Requires `buffer_len == 32`.
  - `int32 signal_zkgroup_group_master_key_deserialize(const uint8_t* bytes, size_t bytes_len, void** out_master_key)`
    - Requires `bytes_len == 32`.

### Usage notes (C-ABI)

- **Output contract**
  - On non-zero status, out pointers are set to null.
- **Panic handling**
  - All exported functions use `catch_unwind` and return status `2` if a panic is caught.
- **Status codes**
  - `0`: success
  - `1`: invalid argument
  - `2`: panic caught
  - `4`: deserialization failure
---

## STEP 1 — Pure domain interface plan (`Percolator.Cryptography`)

### 1.1 Domain value types

Goal: represent Group V2 key material safely, without exposing raw native handles.

- `GroupMasterKey`
  - Implement as a source-generated fixed-size byte value using the existing generator attribute:
    - `[ByteArray(length: 32)]`
  - Required invariants:
    - **length is exactly 32 bytes** (enforced by the generated factory methods)
    - treated as immutable once constructed
  - Construction rules (security-sensitive; must be followed consistently):
    - Use `GroupMasterKey.FromBytesOwned(byte[])` only when the input array is newly allocated and will never be mutated after the call.
      - Example: `GroupMasterKey.FromBytesOwned(proto.Field.ToByteArray())`.
    - Use `GroupMasterKey.FromSpan(ReadOnlySpan<byte>)` when converting from an existing byte-backed value without wanting to transfer ownership.
      - This is the preferred path for converting between `ByteArray`-generated types.
    - If the provenance of the input `byte[]` is unclear (may be pooled/reused/mutated), do not use `FromBytesOwned`.
      - Use `FromSpan` over a defensive copy to preserve immutability assumptions.

- `GroupId`
  - The 32-byte group identifier derived from `GroupMasterKey` via `GroupSecretParams`.
  - Used for server-side group identification and routing.
  - Implement as `[ByteArray(length: 32)]`.

- `BlobKey`
  - The 32-byte symmetric encryption key derived from `GroupMasterKey` via `GroupSecretParams`.
  - Used to encrypt/decrypt group profile metadata (title, avatar, membership roster).
  - Implement as `[ByteArray(length: 32)]`.

- `GroupSecretParams` (ephemeral native handle only)
  - Not exposed in the domain layer.
  - Exists only transiently in the infrastructure layer during derivation operations.
  - Sub-keys (group_id, blob_key) are extracted via `GetGroupId`/`GetBlobKey` and returned as domain value objects.

### 1.2 Domain service interface

Define: `IGroupCryptographyService`

This is the sole domain entrypoint for zkgroup primitives.

Required responsibilities (pure managed contracts only):

- **Generate**
  - Generate a new `GroupMasterKey`.

- **Derive sub-keys**
  - Derive `GroupId` from `GroupMasterKey` (deterministic via ephemeral `GroupSecretParams`).
  - Derive `BlobKey` from `GroupMasterKey` (deterministic via ephemeral `GroupSecretParams`).

- **Serialize / Deserialize**
  - `GroupMasterKey` <-> `byte[]` (32 bytes)
  - All deserialization must validate lengths and throw managed exceptions.

Interface design rule:

- `GroupSecretParams` is never exposed in the domain interface.
- Sub-keys are derived directly from `GroupMasterKey` via infrastructure that manages ephemeral native handles.
- Signal.Interop provides `GetGroupId` and `GetBlobKey` to extract these values without serializing `GroupSecretParams`.

Domain interface design constraints:

- All methods accept/return safe managed types.
- Prefer `ReadOnlySpan<byte>` for inputs and `byte[]` / domain value objects for outputs.
- No `SafeHandle`, no `IntPtr`, no `Memory<T>` pinned requirements.

---

## STEP 2 — Infrastructure bridge plan (`Percolator.Infrastructure`)

### 2.1 Consuming localized Signal interop binaries

Goal: explicitly document and enforce how `Percolator.Infrastructure` references the localized binaries.

- `Signal.Interop.dll` is consumed via a relative assembly reference in the `.csproj`.
- `signal_shim.dll` (and any other required native assets) are copied to the output directory.
- `Percolator.Cryptography` must not reference these binaries.

### 2.2 Implementing `IGroupCryptographyService` via FFI

Goal: implement the pure managed interface by orchestrating `Signal.Interop` safe handles.

Implementation notes (blueprint-level; no code yet):

- The infrastructure implementation will:
  - create and dispose native resources via `SafeHandle` instances (e.g., `GroupSecretParamsSafeHandle`, `GroupMasterKeySafeHandle` as exposed by `Signal.Interop`)
  - ensure cleanup via `using`/`try/finally`
  - capture native status codes
  - translate non-zero native return codes into `CryptographicException` (or a domain-specific exception type if you already have one)

Rehydration rule:

- Infrastructure must support deterministic rehydration of `GroupSecretParams` (and derived identifiers/sub-keys) from a managed 32-byte `GroupMasterKey`.

Boundary safety rules:

- All inputs must be validated **before** passing buffers across the boundary:
  - null checks
  - exact-length checks
  - reject unexpected lengths with managed exceptions
- All outputs copied **out of native** must be:
  - copied into managed `byte[]`
  - validated for expected length
  - immediately released on the native side

---

## Chunk 0 — Signal Group V2 foundations (protocol + persistence + pipeline)

Chunk 0 is a hard gate: no WPF UX work (Chunk A/B) proceeds until the crypto foundation and persistence model are correct.

### 0.1 Scope and guiding principle

- Replace all placeholder “sender-key ratchet” and “control plane” concepts with actual zkgroup primitives.
- Persist only the managed root secret (`GroupMasterKey`) at rest.
- Rehydrate native state only inside `Percolator.Infrastructure`.

### 0.2 Protocol primitives and managed representations

This plan intentionally names the primitives at the architectural level (exact naming may vary based on the interop wrapper surface).

- **GroupMasterKey**
  - A stable 32-byte root secret.
  - Persisted at rest (encrypted).
  - The source of truth for rehydrating all other zkgroup material.

- **GroupSecretParams (ephemeral)**
  - Deterministically derived/rehydrated from `GroupMasterKey` at runtime.
  - Not persisted.

- **Derived sub-keys and identifiers**
  - Derived from rehydrated `GroupSecretParams`:
    - group stable identifier (`group_id` or equivalent)
    - blob key / attachment key material (`blob_key`)
    - any encryption keypairs required by Group V2

Deliverable:

- A clear mapping in domain/application code between:
  - group conversation identity
  - persisted group key material (`GroupMasterKey`, secret params)
  - derived identifiers used in message envelopes

### 0.3 Application pipeline updates (no placeholder control planes)

Goal: define how group messages are produced/consumed using zkgroup-backed key material.

#### 0.3.1 Concrete message/envelope mapping (what exists today vs what must change)

Current state in `Percolator.Contracts` (`Protos/internal_messaging.proto`):

- Group identity is currently represented as `group_conversation_guid` (16-byte GUID).
- Group creation exists as `ChatEnvelope.create_group` (`CreateGroup` message).
- Group chat content is currently carried via `ChatEnvelope.text_message` (`TextMessage`) with optional `group_conversation_guid`.
- There is an existing administrative + key-rotation control-plane surface:
  - `SignedAdminOperation`
  - `KeyDistributionPayload`
  - `SignedKeyAdoptionConfirmation`
  - `SignedAdminCommitOperation`

Required changes for Signal Group V2 (zkgroup):

- Introduce explicit Group V2 message(s) for ciphertext transport.
  - Add a new `ChatEnvelope` oneof case for Group V2 ciphertext (name TBD, e.g. `GroupV2Message`).
  - This message must carry:
    - `group_conversation_guid` (for DB lookup / routing within the app)
    - a derived zkgroup group identifier (`group_id`) if required by the native decrypt/encrypt API
    - the ciphertext bytes (and any metadata required by the interop decrypt routine)
    - optional attachment/blob reference(s) as needed (decrypted via derived `blob_key`)

- Deprecate/remove the existing “Key Rotation Plane” and sender-key distribution concepts.
  - `KeyDistributionPayload`, `SignedKeyAdoptionConfirmation`, `SignedAdminCommitOperation` were designed for the previous placeholder protocol and should not be used for Group V2.
  - Keep `SignedAdminOperation` only if it remains valid for the non-crypto “group management” layer; otherwise replace with Group V2 membership/auth semantics in Chunk B.

Plan invariant:

- Conversation identity in the app remains `group_conversation_guid`.
- zkgroup-derived `group_id` is treated as cryptographic metadata derived from `GroupMasterKey`/`GroupSecretParams`, not as the primary DB key.

Inbound (receive path):

- Parse inbound group envelope.
- Resolve group identity.
- Load the persisted `GroupMasterKey` for the group (read model query; no domain repository dependency on the read path).
- Use `IGroupCryptographyService` to:
  - rehydrate `GroupSecretParams` (managed -> infra -> native) deterministically from `GroupMasterKey`
  - compute required derived values (e.g., `group_id`, `blob_key`) for envelope validation and data decryption
  - decrypt/verify the message according to Group V2 rules
- Persist decrypted plaintext into the existing conversation/message store.

Outbound (send path):

- Resolve group identity and load persisted `GroupMasterKey`.
- Use `IGroupCryptographyService` to:
  - rehydrate `GroupSecretParams` deterministically
  - derive per-message keys/parameters required by Group V2
  - encrypt/sign according to Group V2
- Dispatch using existing message send abstractions.

Important architectural constraint:

- The application layer depends only on `Percolator.Cryptography` abstractions.
- Native interop is not visible outside `Percolator.Infrastructure`.

#### 0.3.2 Epoch transition atomicity (eviction and rotation)

When an epoch rotation occurs (e.g., member eviction), the implementation must ensure atomicity of the epoch cutover with respect to outbound message delivery:

- The outbound message queue must block any concurrent sends to the old epoch while the 1:1 Double Ratchet tunnels are transmitting the new `GroupMasterKey` to remaining members.
- If a message is sent signed with the old epoch parameters during this window:
  - The server's ZK verification layer may reject the proof if it enforces strict epoch numbers.
  - The evicted user may still be able to read the message if they intercept the packet before the epoch cutover is finalized on the relay.

This constraint applies to any protocol-driven epoch update that changes the root key material (not just explicit eviction).

### 0.4 Persistence model (EF Core / SQLite)

Goal: persist only the safe managed `GroupMasterKey` root secret; never persist native handles or derived zkgroup sub-keys.

#### 0.4.1 EF schema decision (what exists today vs what must change)

Current state in `Percolator.Infrastructure.Persistence`:

- `ConversationDbo` contains:
  - `Id` (GUID)
  - `GroupConversationGuid` (nullable GUID)
  - no column for any group cryptographic root secret
- `GroupMemberDbo` exists (`ConversationId`, `MemberSpki`, `Role`, etc.).
  - This currently models membership/admin role in a way that predates Group V2 semantics.
- `SenderKeyDbo` exists (`ConversationId`, `SenderPeerId`, `ChainKey`, `SigningKey`).
  - This is part of the previous placeholder sender-key direction.
  - There is no Sqlite implementation of `IGroupSenderKeyRepository` yet (only `InMemoryGroupSenderKeyRepository`).

Schema decision for Group V2:

- Add a new persistence table dedicated to Group V2 root key material:
  - `GroupCryptoStateDbo`
    - `ConversationId` (PK, FK to `ConversationDbo.Id`)
    - `EncryptedGroupMasterKeyBytes` (BLOB, required; encrypted-at-rest wrapper over exactly 32 bytes)
    - `CreatedAtUtc`, `UpdatedAtUtc` (optional but recommended for auditing/migrations)

Rationale:

- Keeps group crypto material separate from `ConversationDbo` and avoids widening the conversation table with cryptography-specific concerns.
- Makes it explicit that only group conversations have this state.

Migration note:

- `SenderKeys` / key-rotation-plane persistence (and any admin-sequence state that only exists to support the old control plane) should be treated as legacy and either:
  - migrated to Group V2 semantics in a dedicated migration step, or
  - deleted/ignored if this is a breaking protocol migration.

Required persistence (authoritative):

- For each group conversation, persist exactly one encrypted 32-byte master key blob:
  - `GroupCryptoStateDbo.EncryptedGroupMasterKeyBytes`
    - This is the encrypted-at-rest wrapper over the 32-byte serialized `GroupMasterKey`.
    - No zkgroup-derived sub-keys (`GroupSecretParams`, `group_id`, `blob_key`, keypairs) are persisted.

Explicit lifecycle:

- **Create**
  - Infrastructure generates a new `GroupMasterKey` via zkgroup.
  - Extract/serialize `GroupMasterKey` into a managed 32-byte array.
  - Encrypt at rest.
  - Store into `GroupCryptoStateDbo.EncryptedGroupMasterKeyBytes` for the conversation.

- **Load**
  - Read `GroupCryptoStateDbo.EncryptedGroupMasterKeyBytes` for the conversation.
  - Decrypt at rest into a managed 32-byte array.
  - Validate exact length (must be 32 bytes) in managed code.
  - Only then call infrastructure FFI to rehydrate:
    - `GroupMasterKeySafeHandle` (or equivalent)
    - `GroupSecretParamsSafeHandle` (or equivalent) derived deterministically from the master key
    - derived values (`group_id`, `blob_key`, encryption keypairs) for runtime use

- **Rotation / updates**
  - Any protocol-driven update that changes root key material must be represented as a deterministic update of `GroupCryptoStateDbo.EncryptedGroupMasterKeyBytes`.
  - No derived keys are stored; only rederived.

Deliverable:

- A concrete EF schema plan for group key material that supports:
  - restart/recovery
  - deterministic serialization
  - safe handling of corrupt DB state

### 0.5 Testing plan (crypto boundary + persistence safety)

Goal: tests must prove the safety and determinism of the `GroupMasterKey` lifecycle.

Infrastructure-focused tests (must exist before Chunk A):

- **Serialization stability**
  - `GroupMasterKey` serialize -> deserialize yields byte-for-byte equality.
  - Rehydrated `GroupSecretParams` derived from the same `GroupMasterKey` yields stable derived identifiers (e.g., `group_id` is deterministic).

- **Invalid length handling**
  - `GroupMasterKey` constructor rejects non-32-byte input.
  - Infrastructure rejects invalid buffer lengths before calling into native.

- **Corruption handling**
  - Tampered/corrupt persisted blobs cause a managed exception.
  - No native call is made with invalid buffers.

- **At-rest encryption round-trip**
  - Persist encrypted master key bytes -> decrypt -> rehydrate params -> encrypt/decrypt a test message successfully.

### 0.6 Coverage check (end-of-chunk gate)

- All tests pass.
- Boundary validation branches are covered.
- Any native error codes are exercised via controlled failing inputs (where possible) and mapped to managed exceptions.

### 0.7 Dead code to remove (breaking cleanup list)

This section is an explicit checklist of code that becomes obsolete once Group V2 (zkgroup) is the only supported group messaging protocol.

#### Contracts (protobuf)

- Remove/deprecate the “Key Rotation Plane” messages from `Percolator.Contracts/Protos/internal_messaging.proto`:
  - `KeyDistributionPayload` (`ChatEnvelope.key_distribution`)
  - `SignedKeyAdoptionConfirmation` (`ChatEnvelope.key_adoption_confirmation`)
  - `SignedAdminCommitOperation` (`ChatEnvelope.admin_commit_operation`)

#### Application inbound processing

- Remove the inbound `ProcessInternalEnvelopeHandler` cases and commands for the key-rotation plane:
  - `ChatEnvelope.MessageOneofCase.KeyDistribution`
  - `ChatEnvelope.MessageOneofCase.KeyAdoptionConfirmation`
  - `ChatEnvelope.MessageOneofCase.AdminCommitOperation`

#### Domain/Application services tied to sender-key / key-rotation

- Remove sender-key import/storage services:
  - `Percolator.Chat.App.Services.IGroupSenderKeyService`
  - `Percolator.Application.Apps.Chat.GroupSenderKeyService`
  - `Percolator.Application.Apps.Chat.IGroupSenderKeyRepository`
  - `Percolator.Infrastructure.Chat.InMemoryGroupSenderKeyRepository`

- Remove key-distribution handlers/commands that only exist to feed the sender-key repository:
  - `Percolator.Chat.App.Commands.ReceiveKeyDistributionCommand`
  - `Percolator.Chat.App.Handlers.ReceiveKeyDistributionHandler`

- Remove the “adoption confirmation / commit policy” pipeline if it is only used for the old key-rotation plane:
  - `IKeyAdoptionStore` and its handlers (e.g., `ReceiveKeyAdoptionConfirmationHandler`, `KeyAdoptionStoredHandler`)
  - `PostSignedAdminCommitOperationCommand` / events / dispatch handlers used solely to broadcast `SignedAdminCommitOperation`

#### Persistence (EF Core / SQLite)

- Remove legacy sender-key persistence:
  - `SenderKeyDbo` and the `SenderKeys` table

- Remove legacy key-adoption persistence if it exists only for the old plane:
  - `KeyAdoptionConfirmationDbo` and the `KeyAdoptionConfirmations` table

Notes:

- Admin-group management (`SignedAdminOperation`, `GroupAdminKeys`, `GroupAdminOps`, `GroupAdminStates`, etc.) may remain temporarily until Group V2 membership/admin semantics replace it in Chunk B.
  - When Chunk B lands, re-evaluate and delete any remaining admin-sequencing/commit concepts that no longer exist in the Group V2 model.

---

## Chunk A — WPF group UX (blocked on Chunk 0)

Chunk A will be rewritten after Chunk 0 is reviewed and accepted.

For now, Chunk A is explicitly blocked on:

- `IGroupCryptographyService` existing in `Percolator.Cryptography`.
- Infrastructure implementation and persistence schema being complete.
- End-to-end send/receive group messages working through the application pipeline.

---

## Chunk B — Group management UX (blocked on Chunk 0)

Chunk B will be rewritten after Chunk 0 is reviewed and accepted.

For now, Chunk B is explicitly blocked on:

- Group V2 membership/admin semantics being mapped to domain/application operations.
- Persistence schema supporting any required group state for membership changes.
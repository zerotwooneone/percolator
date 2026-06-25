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

    - **`public static GroupPublicParamsSafeHandle GetGroupPublicParams(GroupSecretParamsSafeHandle handle)`**
        - Extracts the group public parameters from secret params.
        - Intended use: server-side verification of presentations.

    - **`public static byte[] SerializeGroupPublicParams(GroupPublicParamsSafeHandle handle)`**
        - Serializes group public parameters to a byte array.
        - Intended use: network transmission to the server.

    - **`public static GroupPublicParamsSafeHandle DeserializeGroupPublicParams(ReadOnlySpan<byte> bytes)`**
        - Reconstructs group public parameters from serialized bytes.

    - **`public static ServerSecretParamsSafeHandle GenerateServerSecretParams(ReadOnlySpan<byte> randomness32)`**
        - Generates server secret parameters from exactly 32 bytes of randomness.
        - Intended use: in-process test loops / private deployments.

    - **`public static ServerPublicParamsSafeHandle GetServerPublicParams(ServerSecretParamsSafeHandle serverSecretParams)`**
        - Extracts the server public parameters from server secret params.

    - **`public static byte[] SerializeServerPublicParams(ServerPublicParamsSafeHandle handle)`**
        - Serializes server public parameters to a byte array.
        - Intended use: network transmission to clients.

    - **`public static ServerPublicParamsSafeHandle DeserializeServerPublicParams(ReadOnlySpan<byte> bytes)`**
        - Reconstructs server public parameters from serialized bytes.

    - **`public static AuthCredentialWithPniResponseSafeHandle IssueAuthCredentialWithPni(ReadOnlySpan<byte> aciBytes16, ReadOnlySpan<byte> pniBytes16, ulong redemptionTimeEpochSeconds, ServerSecretParamsSafeHandle serverSecretParams, ReadOnlySpan<byte> randomness32)`**
        - Server-side issuance step.
        - `aciBytes16`/`pniBytes16` must be exactly 16 bytes each.

    - **`public static AuthCredentialWithPniSafeHandle ReceiveAuthCredentialWithPni(AuthCredentialWithPniResponseSafeHandle response, ReadOnlySpan<byte> aciBytes16, ReadOnlySpan<byte> pniBytes16, ulong redemptionTimeEpochSeconds, ServerPublicParamsSafeHandle serverPublicParams)`**
        - Client-side verification + extraction step.

    - **`public static AuthCredentialWithPniPresentationSafeHandle PresentAuthCredentialWithPni(AuthCredentialWithPniSafeHandle credential, ServerPublicParamsSafeHandle serverPublicParams, GroupSecretParamsSafeHandle groupSecretParams, ReadOnlySpan<byte> randomness32)`**
        - Client-side presentation generation.

    - **`public static void VerifyAuthCredentialWithPniPresentation(AuthCredentialWithPniPresentationSafeHandle presentation, ServerSecretParamsSafeHandle serverSecretParams, GroupPublicParamsSafeHandle groupPublicParams, ulong redemptionTimeEpochSeconds)`**
        - Server-side verification.
        - Throws `CryptographicException` on verification failure.

    - **`public static byte[] SerializeAuthCredentialWithPniPresentation(AuthCredentialWithPniPresentationSafeHandle presentation)`**
        - Serializes an AuthCredentialWithPniPresentation to a byte array.
        - Intended use: transmitting presentations over the network.

    - **`public static AuthCredentialWithPniPresentationSafeHandle DeserializeAuthCredentialWithPniPresentation(ReadOnlySpan<byte> presentationBytes)`**
        - Reconstructs an AuthCredentialWithPniPresentation from serialized bytes.
        - Throws `ArgumentException` if `presentationBytes.Length == 0`.
        - Throws `CryptographicException` if native deserialization fails.

    - **`public static byte[] SerializeGroupMasterKey(GroupMasterKeySafeHandle masterKey)`**
    - **`public static void SerializeGroupMasterKey(GroupMasterKeySafeHandle masterKey, Span<byte> buffer32)`**
        - Serializes a master key into exactly 32 bytes.
        - The `Span<byte>` overload is the allocation-free option.
        - Throws `ArgumentException` if the destination buffer length is not exactly 32.

    - **`public static GroupMasterKeySafeHandle DeserializeGroupMasterKey(ReadOnlySpan<byte> bytes32)`**
        - Reconstructs a master key handle from exactly 32 serialized bytes.
        - Throws `ArgumentException` if `bytes32.Length != 32`.
        - Throws `CryptographicException` if native deserialization fails.

    - **`public static void GetServerGroupId(GroupSecretParamsSafeHandle handle, Span<byte> outBuffer)`**
        - Writes exactly 32 bytes of the server group identifier for Blind Relay routing.
        - Allocation-free.
        - Throws `ArgumentException` if the destination buffer length is not exactly 32.

    - **`public static int SerializeSenderKeyRecord(SenderKeyRecordSafeHandle record, Span<byte> buffer)`**
        - Serializes a SenderKeyRecord to a pre-allocated buffer.
        - **Security**: This prevents leaving key material in the managed heap.
        - Caller must provide a buffer of sufficient size (call `signal_protocol_sender_key_record_serialize_len` in native code to determine size).

    - **`public static SenderKeyRecordSafeHandle DeserializeSenderKeyRecord(ReadOnlySpan<byte> bytes)`**
        - Reconstructs a SenderKeyRecord from serialized bytes.
        - Throws `ArgumentException` if `bytes.Length == 0`.
        - Throws `CryptographicException` if native deserialization fails.

    - **`public static SenderAddressSafeHandle NewSenderAddress(ReadOnlySpan<byte> uuidBytes, uint deviceId)`**
        - Creates a new SenderAddress from a UUID and device ID.
        - `uuidBytes` must be exactly 16 bytes.
        - `deviceId` must be in the range 1-127 (Signal protocol constraint).

    - **`public static Guid GetSenderAddressName(IntPtr address)`**
        - Extracts the 16-byte UUID (`PeerId`) from a raw `IntPtr` address handle.
        - Intended use: extracting the target user identity inside a VTable `SenderKeyStore` callback.

    - **`public static uint GetSenderAddressDeviceId(IntPtr address)`**
        - Extracts the integer `DeviceId` from a raw `IntPtr` address handle.
        - Intended use: extracting the target device inside a VTable `SenderKeyStore` callback.

    - **`public static SenderKeyDistributionMessageSafeHandle CreateSenderKeyDistributionMessage(IntPtr storeVTable, SenderAddressSafeHandle sender, Guid distributionId)`**
        - Creates a SenderKeyDistributionMessage for distributing a sender key to a new group member.
        - Uses the VTable-based SenderKeyStore for key state management.

    - **`public static void ProcessSenderKeyDistributionMessage(IntPtr storeVTable, SenderAddressSafeHandle sender, SenderKeyDistributionMessageSafeHandle message)`**
        - Processes a received SenderKeyDistributionMessage to update the local SenderKeyRecord.
        - Uses the VTable-based SenderKeyStore for key state management.

    - **`public static uint GetKeyId(SenderKeyMessageSafeHandle message)`**
        - Extracts the key ID (chain ID) from a SenderKeyMessage.

    - **`public static SenderKeyMessageSafeHandle EncryptGroupMessage(IntPtr storeVTable, SenderAddressSafeHandle sender, Guid distributionId, ReadOnlySpan<byte> plaintext)`**
        - Encrypts a plaintext message for a group using the SenderKeyRecord.
        - Returns the encrypted message.
        - Uses the VTable-based SenderKeyStore for key state management.

    - **`public static byte[] DecryptGroupMessage(IntPtr storeVTable, SenderAddressSafeHandle sender, SenderKeyMessageSafeHandle message)`**
        - Decrypts a group message using the SenderKeyRecord.
        - Returns the plaintext.
        - Uses the VTable-based SenderKeyStore for key state management.

    - **`public static byte[] SerializeSenderKeyDistributionMessage(SenderKeyDistributionMessageSafeHandle message)`**
        - Serializes a SenderKeyDistributionMessage to a byte array.
        - Intended use: persisting or transmitting distribution messages.

    - **`public static SenderKeyDistributionMessageSafeHandle DeserializeSenderKeyDistributionMessage(ReadOnlySpan<byte> bytes)`**
        - Reconstructs a SenderKeyDistributionMessage from serialized bytes.
        - Throws `ArgumentException` if `bytes.Length == 0`.
        - Throws `CryptographicException` if native deserialization fails.

    - **`public static byte[] SerializeSenderKeyMessage(SenderKeyMessageSafeHandle message)`**
        - Serializes a SenderKeyMessage to a byte array.
        - Intended use: persisting or transmitting encrypted group messages.

    - **`public static SenderKeyMessageSafeHandle DeserializeSenderKeyMessage(ReadOnlySpan<byte> bytes)`**
        - Reconstructs a SenderKeyMessage from serialized bytes.
        - Throws `ArgumentException` if `bytes.Length == 0`.
        - Throws `CryptographicException` if native deserialization fails.

    - **`public static void GenerateEd25519KeyPair(Span<byte> outPrivateKey32, Span<byte> outPublicKey32)`**
        - Generates a new Ed25519 key pair for Sealed Sender protocol.
        - `outPrivateKey32` and `outPublicKey32` must be exactly 32 bytes each.
        - Allocation-free.
        - Throws `ArgumentException` if output spans are not exactly 32 bytes.
        - Throws `CryptographicException` if key generation fails.

    - **`public static void Ed25519Sign(ReadOnlySpan<byte> privateKey32, ReadOnlySpan<byte> message, Span<byte> outSignature64)`**
        - Signs a message using an Ed25519 private key.
        - `privateKey32` must be exactly 32 bytes.
        - `outSignature64` must be exactly 64 bytes.
        - Allocation-free.
        - Throws `ArgumentException` if input/output spans have incorrect lengths.
        - Throws `CryptographicException` if signing fails.

    - **`public static bool Ed25519Verify(ReadOnlySpan<byte> publicKey32, ReadOnlySpan<byte> message, ReadOnlySpan<byte> signature64)`**
        - Verifies an Ed25519 signature against a message and public key.
        - `publicKey32` must be exactly 32 bytes.
        - `signature64` must be exactly 64 bytes.
        - Returns `true` if the signature is valid, `false` otherwise.
        - Throws `ArgumentException` if input spans have incorrect lengths.

    - **`public const int Ed25519PrivateKeyLength = 32`**
    - **`public const int Ed25519PublicKeyLength = 32`**
    - **`public const int Ed25519SignatureLength = 64`**

SafeHandle types (opaque native ownership):

- **`public sealed class GroupSecretParamsSafeHandle : SafeHandle`**
- **`public sealed class GroupMasterKeySafeHandle : SafeHandle`**
- **`public sealed class GroupPublicParamsSafeHandle : SafeHandle`**
- **`public sealed class ServerSecretParamsSafeHandle : SafeHandle`**
- **`public sealed class ServerPublicParamsSafeHandle : SafeHandle`**
- **`public sealed class AuthCredentialWithPniResponseSafeHandle : SafeHandle`**
- **`public sealed class AuthCredentialWithPniSafeHandle : SafeHandle`**
- **`public sealed class AuthCredentialWithPniPresentationSafeHandle : SafeHandle`**
- **`public sealed class SenderKeyRecordSafeHandle : SafeHandle`**
- **`public sealed class SenderAddressSafeHandle : SafeHandle`**
- **`public sealed class SenderKeyDistributionMessageSafeHandle : SafeHandle`**
- **`public sealed class SenderKeyMessageSafeHandle : SafeHandle`**

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

- **VTable Delegate Lifetime**
    - The unmanaged function pointers (delegates) backing the `storeVTable` IntPtr MUST be kept alive by the managed application (e.g., stored in a class-level field) for the entire duration of the native call to prevent the Garbage Collector from cleaning them up before the native callback executes.
    - Failure to keep delegates alive will result in a fatal Execution Engine crash (Access Violation) when the Rust FFI attempts to invoke the callback.

- **SenderKeyStore VTable Construction**
    - Signal.Interop does NOT provide a helper method like `CreateSenderKeyStore`. You must manually construct the VTable struct and pin it in memory.
    - The VTable struct layout is defined in `SenderKeyStoreVTable.cs`:
        ```csharp
        [StructLayout(LayoutKind.Sequential)]
        public struct SenderKeyStoreVTable
        {
            public IntPtr LoadSenderKey;
            public IntPtr StoreSenderKey;
        }
        ```
    - Required delegate signatures:
        ```csharp
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public unsafe delegate int LoadSenderKeyDelegate(
            IntPtr senderAddress,
            byte* distributionIdBytes,
            UIntPtr distributionIdLen,
            out IntPtr outRecord,
            out UIntPtr outLen
        );

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public unsafe delegate int StoreSenderKeyDelegate(
            IntPtr senderAddress,
            byte* distributionIdBytes,
            UIntPtr distributionIdLen,
            byte* recordBytes,
            UIntPtr recordLen
        );
        ```
    - **VTable Creation Pattern** (from `InMemorySenderKeyStore.cs`):
        ```csharp
        public unsafe IntPtr CreateVTable()
        {
            // 1. Create delegate instances and keep them alive
            _loadDelegate = LoadKey;
            _storeDelegate = StoreKey;
            
            // 2. Pin the delegates to prevent GC
            _loadDelegateHandle = GCHandle.Alloc(_loadDelegate);
            _storeDelegateHandle = GCHandle.Alloc(_storeDelegate);
            
            // 3. Create function pointers
            IntPtr loadPtr = Marshal.GetFunctionPointerForDelegate(_loadDelegate);
            IntPtr storePtr = Marshal.GetFunctionPointerForDelegate(_storeDelegate);
            
            // 4. Create and pin the VTable struct
            var vTable = new SenderKeyStoreVTable
            {
                LoadSenderKey = loadPtr,
                StoreSenderKey = storePtr
            };
            
            _vTableHandle = GCHandle.Alloc(vTable, GCHandleType.Pinned);
            return _vTableHandle.AddrOfPinnedObject();
        }
        ```
    - **Memory Management**: You must free all GCHandles when disposing your store:
        ```csharp
        public void Dispose()
        {
            if (_loadDelegateHandle.IsAllocated) _loadDelegateHandle.Free();
            if (_storeDelegateHandle.IsAllocated) _storeDelegateHandle.Free();
            if (_vTableHandle.IsAllocated) _vTableHandle.Free();
        }
        ```
    - The returned `IntPtr` from `CreateVTable()` is passed to methods like `CreateSenderKeyDistributionMessage(IntPtr storeVTable, ...)`.
    - **Extracting Address Data in Callbacks**: When the native library invokes your delegates, it provides an `IntPtr senderAddress`. You can extract the `UUID` and `DeviceId` using the provided helpers to route the database query:
        ```csharp
        public unsafe int LoadKey(IntPtr senderAddress, byte* distId, UIntPtr distLen, out IntPtr record, out UIntPtr len)
        {
            // Extract the original identifiers
            Guid peerId = SignalCrypto.GetSenderAddressName(senderAddress);
            uint deviceId = SignalCrypto.GetSenderAddressDeviceId(senderAddress);
            Guid distributionId = new Guid(new ReadOnlySpan<byte>(distId, (int)distLen));
            
            // ... look up in DB using peerId, deviceId, and distributionId
        }
        ```

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
    - `int32 signal_zkgroup_group_secret_params_get_server_group_id(const void* secret_params, uint8_t* out_buffer, size_t buffer_len)`
        - Requires `buffer_len == 32`.
        - Returns the server group identifier for Blind Relay routing.
    - `int32 signal_zkgroup_group_secret_params_get_blob_key(const void* secret_params, uint8_t* out_buffer, size_t buffer_len)`
        - Requires `buffer_len == 32`.
    - `int32 signal_zkgroup_group_secret_params_get_public_params(const void* secret_params, void** out_public_params)`
    - `void signal_zkgroup_group_public_params_free(void* public_params)`
    - `int32 signal_zkgroup_group_public_params_get_serialized_len(const void* public_params, size_t* out_len)`
    - `int32 signal_zkgroup_group_public_params_serialize(const void* public_params, uint8_t* out_buffer, size_t buffer_len)`
    - `int32 signal_zkgroup_group_public_params_deserialize(const uint8_t* bytes, size_t bytes_len, void** out_public_params)`

- **Server params (Option B)**
    - `int32 signal_zkgroup_server_secret_params_generate(const uint8_t* randomness32, size_t randomness_len, void** out_server_secret_params)`
    - `void signal_zkgroup_server_secret_params_free(void* server_secret_params)`
    - `int32 signal_zkgroup_server_secret_params_get_public_params(const void* server_secret_params, void** out_server_public_params)`
    - `void signal_zkgroup_server_public_params_free(void* server_public_params)`
    - `int32 signal_zkgroup_server_public_params_get_serialized_len(const void* server_public_params, size_t* out_len)`
    - `int32 signal_zkgroup_server_public_params_serialize(const void* server_public_params, uint8_t* out_buffer, size_t buffer_len)`
    - `int32 signal_zkgroup_server_public_params_deserialize(const uint8_t* bytes, size_t bytes_len, void** out_server_public_params)`

- **AuthCredentialWithPni (ZKC, Option B)**
    - `int32 signal_zkgroup_auth_credential_with_pni_issue_credential(const uint8_t* aci16, size_t aci_len, const uint8_t* pni16, size_t pni_len, uint64_t redemption_time_epoch_seconds, const void* server_secret_params, const uint8_t* randomness32, size_t randomness_len, void** out_response)`
    - `void signal_zkgroup_auth_credential_with_pni_response_free(void* response)`
    - `int32 signal_zkgroup_auth_credential_with_pni_response_receive(const void* response, const uint8_t* aci16, size_t aci_len, const uint8_t* pni16, size_t pni_len, uint64_t redemption_time_epoch_seconds, const void* server_public_params, void** out_credential)`
    - `void signal_zkgroup_auth_credential_with_pni_free(void* credential)`
    - `int32 signal_zkgroup_auth_credential_with_pni_present(const void* credential, const void* server_public_params, const void* group_secret_params, const uint8_t* randomness32, size_t randomness_len, void** out_presentation)`
    - `void signal_zkgroup_auth_credential_with_pni_presentation_free(void* presentation)`
    - `int32 signal_zkgroup_auth_credential_with_pni_presentation_verify(const void* presentation, const void* server_secret_params, const void* group_public_params, uint64_t redemption_time_epoch_seconds)`

- **GroupMasterKey persistence**
    - `int32 signal_zkgroup_group_master_key_serialize(const void* master_key, uint8_t* out_buffer, size_t buffer_len)`
        - Requires `buffer_len == 32`.
    - `int32 signal_zkgroup_group_master_key_deserialize(const uint8_t* bytes, size_t bytes_len, void** out_master_key)`
        - Requires `bytes_len == 32`.

- **SenderKeyRecord (Group Messaging)**
    - `void signal_protocol_sender_key_record_free(void* record)`
    - `int32 signal_protocol_sender_key_record_serialize_len(const void* record, size_t* out_len)`
    - `int32 signal_protocol_sender_key_record_serialize(const void* record, uint8_t* out_buffer, size_t buffer_len)`
    - `int32 signal_protocol_sender_key_record_deserialize(const uint8_t* bytes, size_t bytes_len, void** out_record)`

- **SenderAddress (Group Messaging)**
    - `int32 signal_protocol_sender_address_new(const uint8_t* uuid_bytes, size_t uuid_len, uint32_t device_id, void** out_address)`
        - `uuid_len` must be exactly 16.
        - `device_id` must be in the range 1-127.
    - `void signal_protocol_sender_address_free(void* address)`

- **SenderKeyDistributionMessage (Group Messaging)**
    - `void signal_protocol_sender_key_distribution_message_free(void* message)`
    - `int32 signal_protocol_sender_key_distribution_message_serialize_len(const void* message, size_t* out_len)`
    - `int32 signal_protocol_sender_key_distribution_message_serialize(const void* message, uint8_t* out_buffer, size_t buffer_len)`
    - `int32 signal_protocol_sender_key_distribution_message_deserialize(const uint8_t* bytes, size_t bytes_len, void** out_message)`
    - `int32 signal_protocol_sender_key_distribution_message_create(const void* vtable, const void* sender_address, const uint8_t* distribution_id_bytes, size_t distribution_id_len, void** out_message)`
        - Creates a distribution message using GroupSessionBuilder with VTable-based SenderKeyStore.
    - `int32 signal_protocol_sender_key_distribution_message_process(const void* vtable, const void* sender_address, const void* message)`
        - Processes a distribution message using VTable-based SenderKeyStore.

- **SenderKeyMessage (Group Messaging)**
    - `void signal_protocol_sender_key_message_free(void* message)`
    - `int32 signal_protocol_sender_key_message_serialize_len(const void* message, size_t* out_len)`
    - `int32 signal_protocol_sender_key_message_serialize(const void* message, uint8_t* out_buffer, size_t buffer_len)`
    - `int32 signal_protocol_sender_key_message_deserialize(const uint8_t* bytes, size_t bytes_len, void** out_message)`
    - `int32 signal_protocol_sender_key_message_get_key_id(const void* message, uint32_t* out_key_id)`
        - Returns the chain ID as a proxy for key ID.

- **GroupCipher (Group Messaging)**
    - `int32 signal_protocol_group_cipher_encrypt(const void* vtable, const void* sender_address, const uint8_t* distribution_id_bytes, size_t distribution_id_len, const uint8_t* plaintext, size_t plaintext_len, void** out_message)`
        - Encrypts a message using GroupCipher with VTable-based SenderKeyStore.
    - `int32 signal_protocol_group_cipher_decrypt(const void* vtable, const void* sender_address, const void* message, uint8_t** out_plaintext, size_t* out_plaintext_len)`
        - Decrypts a message using GroupCipher with VTable-based SenderKeyStore.
    - `void signal_protocol_group_cipher_free_plaintext(uint8_t* plaintext, size_t len)`

- **Ed25519 (Sealed Sender)**
    - `int32 signal_crypto_ed25519_generate_key_pair(uint8_t* out_private_key, uint8_t* out_public_key)`
        - Generates a new Ed25519 key pair.
        - `out_private_key` and `out_public_key` must point to 32-byte buffers.
        - Allocation-free; caller allocates buffers.
    - `int32 signal_crypto_ed25519_sign(const uint8_t* private_key_bytes, const uint8_t* message_bytes, size_t message_len, uint8_t* out_signature)`
        - Signs a message using an Ed25519 private key.
        - `private_key_bytes` must be exactly 32 bytes.
        - `out_signature` must point to a 64-byte buffer.
        - Allocation-free; caller allocates buffer.
    - `int32 signal_crypto_ed25519_verify(const uint8_t* public_key_bytes, const uint8_t* message_bytes, size_t message_len, const uint8_t* signature_bytes)`
        - Verifies an Ed25519 signature.
        - `public_key_bytes` must be exactly 32 bytes.
        - `signature_bytes` must be exactly 64 bytes.
        - Returns `0` on success, `3` on verification failure.

### Usage notes (C-ABI)

- **Output contract**
    - On non-zero status, out pointers are set to null.
- **Panic handling**
    - All exported functions use `catch_unwind` and return status `2` if a panic is caught.
- **Status codes**
    - `0`: success
    - `1`: invalid argument
    - `2`: panic caught
    - `3`: verification failure
    - `4`: deserialization failure
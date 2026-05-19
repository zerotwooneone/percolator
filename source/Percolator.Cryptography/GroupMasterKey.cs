using Percolator.Cryptography.Primitives;
using Percolator.SourceGenerators;

namespace Percolator.Cryptography;

/// <summary>
/// The 32-byte root secret for a Signal Group V2 conversation.
/// This is the only persisted cryptographic state for a group; all derived
/// sub-keys (GroupSecretParams, group_id, blob_key, encryption keypairs)
/// are deterministically rehydrated from this value on demand.
/// </summary>
[ByteArray(length: 32)]
public sealed partial record GroupMasterKey;

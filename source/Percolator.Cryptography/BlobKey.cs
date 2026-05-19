using Percolator.Cryptography.Primitives;
using Percolator.SourceGenerators;

namespace Percolator.Cryptography;

/// <summary>
/// The 32-byte symmetric encryption key derived from GroupMasterKey via GroupSecretParams.
/// Used to encrypt/decrypt group profile metadata (title, avatar, membership roster).
/// </summary>
[ByteArray(length: 32)]
public sealed partial record BlobKey;

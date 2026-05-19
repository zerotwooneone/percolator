using Percolator.Cryptography.Primitives;
using Percolator.SourceGenerators;

namespace Percolator.Cryptography;

/// <summary>
/// The 32-byte group identifier derived from GroupMasterKey via GroupSecretParams.
/// Used for server-side group identification and routing.
/// </summary>
[ByteArray(length: 32)]
public sealed partial record GroupId;

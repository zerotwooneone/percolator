using Percolator.SourceGenerators;

namespace Percolator.Network;

/// <summary>
/// A DDD value type representing the unique and stable hash of a public key.
/// This is used as the canonical identifier for a peer.
/// </summary>
[ByteArray(length: 32)]
public sealed partial record PublicKeyHash;

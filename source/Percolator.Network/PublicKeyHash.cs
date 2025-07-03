using Percolator.Network.Primitives;

namespace Percolator.Network;

/// <summary>
/// A DDD value type representing the unique and stable hash of a public key.
/// This is used as the canonical identifier for a peer.
/// </summary>
public record PublicKeyHash(byte[] Value) : ByteArrayRecord(Value);

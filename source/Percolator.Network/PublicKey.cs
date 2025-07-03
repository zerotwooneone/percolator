using Percolator.Network.Primitives;

namespace Percolator.Network;

/// <summary>
/// A DDD value type representing a public key.
/// </summary>
public record PublicKey(byte[] Value) : ByteArrayRecord(Value);

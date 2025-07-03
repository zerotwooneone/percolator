using Percolator.Network.Primitives;

namespace Percolator.Network;

/// <summary>
/// A DDD value type representing a cryptographic signature.
/// </summary>
public record Signature(byte[] Value) : ByteArrayRecord(Value);

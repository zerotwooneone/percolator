using Percolator.Network.Primitives;

namespace Percolator.Network;

/// <summary>
/// A DDD value type representing a generic payload of bytes.
/// </summary>
public record Payload(byte[] Value) : ByteArrayRecord(Value);

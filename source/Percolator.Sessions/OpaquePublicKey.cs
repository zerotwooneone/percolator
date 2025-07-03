using Percolator.Sessions.Primitives;

namespace Percolator.Sessions;

/// <summary>
/// A DDD value type representing a public key in a session context.
/// </summary>
public record OpaquePublicKey(byte[] Value) : ByteArrayRecord(Value);

using Percolator.Sessions.Primitives;

namespace Percolator.Sessions;

/// <summary>
/// A DDD value type representing a session key.
/// </summary>
public record OpaqueSessionKey(byte[] Value) : ByteArrayRecord(Value);

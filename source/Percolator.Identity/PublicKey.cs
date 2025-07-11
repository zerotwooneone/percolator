using Percolator.Identity.Primitives;

namespace Percolator.Identity;

/// <summary>
/// A DDD value type representing a PFX certificate blob.
/// </summary>
public record PublicKey(byte[] Value) : ByteArrayRecord(Value);

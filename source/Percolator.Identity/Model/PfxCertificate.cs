using Percolator.Identity.Primitives;

namespace Percolator.Identity.Model;

/// <summary>
/// A DDD value type representing a PFX certificate blob.
/// </summary>
public record PfxCertificate(byte[] Value) : ByteArrayRecord(Value);

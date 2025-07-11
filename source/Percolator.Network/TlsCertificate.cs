using Percolator.Network.Primitives;

namespace Percolator.Network;

/// <summary>
/// Represents a trusted TLS certificate.
/// This is a value object.
/// </summary>
public record TlsCertificate(byte[] RawData) : ByteArrayRecord(RawData);

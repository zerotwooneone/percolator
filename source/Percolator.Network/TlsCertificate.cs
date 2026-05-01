using Percolator.SourceGenerators;

namespace Percolator.Network;

/// <summary>
/// Represents a trusted TLS certificate.
/// This is a value object.
/// </summary>
[ByteArray(minLength: 1, maxLength: 50000)]
public sealed partial record TlsCertificate;

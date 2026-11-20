using Percolator.SourceGenerators;

namespace Percolator.Domain.Delivery.ValueObjects;

/// <summary>
/// 32-byte SHA-256 thumbprint pinning a relay's transport certificate.
/// Public (non-secret) value object; equality uses a fixed-time comparison to avoid
/// timing side channels when validating a relay's presented certificate.
/// </summary>
[ByteArray(length: 32)]
public sealed partial record CertificatePin;

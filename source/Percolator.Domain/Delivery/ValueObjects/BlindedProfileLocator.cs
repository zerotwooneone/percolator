using Percolator.Domain.Identities.ValueObjects;
using Percolator.Domain.Security.Ports;
using Percolator.Domain.Security.ValueObjects;
using Percolator.SourceGenerators;

namespace Percolator.Domain.Delivery.ValueObjects;

/// <summary>
/// 32-byte opaque locator acting as the primary key under which encrypted profile packages are indexed on a Relay or direct peer endpoint.
/// Derived via HKDF-SHA256 from the owner's IdentityKey and ProfileKey.
/// </summary>
[ByteArray(length: 32)]
public sealed partial record BlindedProfileLocator
{
    public static BlindedProfileLocator Derive(IdentityKey ownerPublicKey, ProfileKey profileKey, ICryptoEngine engine)
    {
        ArgumentNullException.ThrowIfNull(ownerPublicKey);
        ArgumentNullException.ThrowIfNull(profileKey);
        ArgumentNullException.ThrowIfNull(engine);

        var bytes = engine.HkdfSha256(
            ownerPublicKey.Span,
            profileKey.Span,
            "PercolatorProfileLocatorV2"u8,
            32);

        return FromSpan(bytes);
    }
}

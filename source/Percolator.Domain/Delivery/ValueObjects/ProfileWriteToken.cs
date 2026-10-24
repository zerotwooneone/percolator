using Percolator.Domain.Identities.ValueObjects;
using Percolator.Domain.Security.Ports;
using Percolator.Domain.Security.ValueObjects;
using Percolator.SourceGenerators;

namespace Percolator.Domain.Delivery.ValueObjects;

/// <summary>
/// 32-byte write authorization credential required to create, update, or delete a profile slot on a Relay.
/// Derived via HKDF-SHA256 from the owner's IdentityKey and ProfileKey.
/// </summary>
[ByteArray(length: 32)]
public sealed partial record ProfileWriteToken
{
    public static ProfileWriteToken Derive(IdentityKey ownerPublicKey, ProfileKey profileKey, ICryptoEngine engine)
    {
        ArgumentNullException.ThrowIfNull(ownerPublicKey);
        ArgumentNullException.ThrowIfNull(profileKey);
        ArgumentNullException.ThrowIfNull(engine);

        var bytes = engine.HkdfSha256(
            ownerPublicKey.Span,
            profileKey.Span,
            "PercolatorProfileWriteTokenV1"u8,
            32);

        return FromSpan(bytes);
    }
}

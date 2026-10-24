using Percolator.Domain.Security.Ports;
using Percolator.Domain.Security.ValueObjects;
using Percolator.SourceGenerators;

namespace Percolator.Domain.Delivery.ValueObjects;

/// <summary>
/// 32-byte write authorization credential required to create, update, or delete a profile slot on a Relay.
/// Derived via HKDF-SHA256 from the owner's private identity key and ProfileKey.
/// Because contacts only possess the public IdentityKey, holding a ProfileKey grants read capability only
/// and strictly prevents contacts from forging the write token to modify or delete the slot.
/// </summary>
[ByteArray(length: 32)]
public sealed partial record ProfileWriteToken
{
    public static ProfileWriteToken Derive(EphemeralPrivateKey ownerPrivateKey, ProfileKey profileKey, ICryptoEngine engine)
    {
        ArgumentNullException.ThrowIfNull(ownerPrivateKey);
        ArgumentNullException.ThrowIfNull(profileKey);
        ArgumentNullException.ThrowIfNull(engine);

        var bytes = engine.HkdfSha256(
            ownerPrivateKey.Span,
            profileKey.Span,
            "PercolatorProfileWriteTokenV1"u8,
            32);

        return FromSpan(bytes);
    }
}

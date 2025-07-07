using System.Security.Cryptography;
using FluentAssertions;
using Percolator.Cryptography;

namespace Percolator.CryptographyTests;

[TestFixture]
public class X3DHManagerTests
{
    [Test]
    public void FullHandshake_ShouldResultInSameSharedSecret()
    {
        // Arrange
        var manager = new X3DHManager();

        // --- Generate keys for both parties ---
        // Alice (initiator)
        using var aliceIdentitySigningKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var aliceIdentityAgreementKey = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        using var aliceEphemeralKey = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);

        // Bob (responder)
        using var bobIdentitySigningKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var bobIdentityAgreementKey = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        using var bobSignedPreKey = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        using var bobOneTimePreKey = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);

        // --- Bob creates his pre-key bundle ---
        var bobSignedPreKeyPublicKey = new PublicKey(bobSignedPreKey.PublicKey.ExportSubjectPublicKeyInfo());
        var bobSignature = manager.SignPreKey(bobIdentitySigningKey, bobSignedPreKeyPublicKey);

        var bobPreKeyBundle = new PreKeyBundle(
            IdentityAgreementKey: bobIdentityAgreementKey.PublicKey.ExportSubjectPublicKeyInfo(),
            IdentitySigningKey: bobIdentitySigningKey.ExportSubjectPublicKeyInfo(),
            SignedPreKey: bobSignedPreKeyPublicKey.Value,
            Signature: bobSignature.Value,
            OneTimePreKey: bobOneTimePreKey.PublicKey.ExportSubjectPublicKeyInfo()
            );

        // --- Alice initiates the handshake ---
        // Alice receives Bob's bundle and initiates the handshake
        var aliceSharedSecret = manager.InitiateHandshake(
            bobPreKeyBundle,
            aliceEphemeralKey,
            aliceIdentityAgreementKey
            );

        // --- Bob responds to the handshake ---
        // Bob receives Alice's identity and ephemeral keys
        var bobSharedSecret = manager.RespondToHandshake(
            new PublicKey(aliceIdentityAgreementKey.PublicKey.ExportSubjectPublicKeyInfo()),
            new PublicKey(aliceEphemeralKey.PublicKey.ExportSubjectPublicKeyInfo()),
            bobIdentitySigningKey,
            bobIdentityAgreementKey,
            bobSignedPreKey,
            bobOneTimePreKey
            );

        // Assert
        aliceSharedSecret.Should().NotBeNull();
        bobSharedSecret.Should().NotBeNull();
        aliceSharedSecret.Value.Should().BeEquivalentTo(bobSharedSecret.Value);
    }
}

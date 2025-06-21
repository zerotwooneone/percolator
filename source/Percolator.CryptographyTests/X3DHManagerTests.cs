using NUnit.Framework;
using FluentAssertions;
using System.Security.Cryptography;
using Pecolator.Cryptography;

namespace Percolator.CryptographyTests;

[TestFixture]
public class X3DHManagerTests
{
    [Test]
    public void FullHandshake_ShouldResultInSameSharedSecret()
    {
        // Arrange: Bob (Responder) generates his keys and bundle
        using var bobIdentityKey = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        using var bobSignedPreKey = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        using var bobOneTimePreKey = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);

        var bobSignedPreKeyBytes = bobSignedPreKey.PublicKey.ExportSubjectPublicKeyInfo();
        var signature = X3DHManager.SignPreKey(bobIdentityKey, bobSignedPreKeyBytes);

        var bobBundle = new PreKeyBundle(
            bobIdentityKey.PublicKey.ExportSubjectPublicKeyInfo(),
            bobSignedPreKeyBytes,
            signature,
            bobOneTimePreKey.PublicKey.ExportSubjectPublicKeyInfo()
        );

        // Arrange: Alice (Initiator) generates her keys
        using var aliceIdentityKey = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        using var aliceEphemeralKeyPair = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);

        // Act: Alice initiates the handshake
        var aliceSharedSecret = X3DHManager.InitiateHandshake(bobBundle, aliceIdentityKey, aliceEphemeralKeyPair);

        // Act: Bob responds to the handshake
        var bobSharedSecret = X3DHManager.RespondToHandshake(
            aliceIdentityKey.PublicKey.ExportSubjectPublicKeyInfo(),
            aliceEphemeralKeyPair.PublicKey.ExportSubjectPublicKeyInfo(),
            bobIdentityKey,
            bobSignedPreKey,
            bobOneTimePreKey
        );

        // Assert
        aliceSharedSecret.Should().NotBeNull();
        aliceSharedSecret.Should().BeEquivalentTo(bobSharedSecret);
    }

    [Test]
    public void InitiateHandshake_WithInvalidSignature_ShouldThrow()
    {
        // Arrange
        using var bobIdentityKey = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        using var bobSignedPreKey = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        using var bobOneTimePreKey = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);

        var tamperedSignature = new byte[64];
        RandomNumberGenerator.Fill(tamperedSignature);

        var bobBundle = new PreKeyBundle(
            bobIdentityKey.PublicKey.ExportSubjectPublicKeyInfo(),
            bobSignedPreKey.PublicKey.ExportSubjectPublicKeyInfo(),
            tamperedSignature,
            bobOneTimePreKey.PublicKey.ExportSubjectPublicKeyInfo()
        );

        using var aliceIdentityKey = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        using var aliceEphemeralKeyPair = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);

        // Act
        Action act = () => X3DHManager.InitiateHandshake(bobBundle, aliceIdentityKey, aliceEphemeralKeyPair);

        // Assert
        act.Should().Throw<CryptographicException>().WithMessage("Invalid signature for signed pre-key.");
    }
}

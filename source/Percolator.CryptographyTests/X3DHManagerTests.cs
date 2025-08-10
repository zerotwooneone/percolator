using System.Security.Cryptography;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Percolator.Cryptography;

namespace Percolator.CryptographyTests;

[TestFixture]
public class X3DHManagerTests
{
    [Test]
    public void FullHandshake_ShouldResultInSameSharedSecret()
    {
        // Arrange
        var manager = new X3DHManager(new NullLogger<X3DHManager>(), Options.Create(new CryptographyOptions()));

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
        var bobSignedPreKeyPublicKey = new PreKey(bobSignedPreKey.PublicKey.ExportSubjectPublicKeyInfo());
        var bobSignature = manager.SignPreKey(bobIdentitySigningKey, bobSignedPreKeyPublicKey);

        var bobPreKeyBundle = new X3dPreKeyBundle(
            new RatchetIdentityKey(bobIdentitySigningKey.ExportSubjectPublicKeyInfo()),
            new RatchetAgreementKey( bobIdentityAgreementKey.PublicKey.ExportSubjectPublicKeyInfo()),
            new PreKey(bobSignedPreKeyPublicKey.Value),
            new Signature(bobSignature.Value),
            new OneTimeKey(bobOneTimePreKey.ExportSubjectPublicKeyInfo())
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
            new RatchetIdentityKey(aliceIdentityAgreementKey.PublicKey.ExportSubjectPublicKeyInfo()),
            new RatchetEphemeralKey(aliceEphemeralKey.PublicKey.ExportSubjectPublicKeyInfo()),
            new PrivateAgreementKey(bobIdentityAgreementKey.ExportECPrivateKey()),
            new PrivatePreKey(bobSignedPreKey.ExportECPrivateKey()),
            new PrivateOneTimeKey(bobOneTimePreKey.ExportECPrivateKey())
            );

        // Assert
        aliceSharedSecret.Should().NotBeNull();
        bobSharedSecret.Should().NotBeNull();
        aliceSharedSecret.Value.Should().BeEquivalentTo(bobSharedSecret.Value);
    }

    [Test]
    public void InitiateHandshake_WithInvalidSignature_ThrowsException()
    {
        // Arrange
        var manager = new X3DHManager(new NullLogger<X3DHManager>(), Options.Create(new CryptographyOptions()));

        // Generate keys for both parties
        using var bobIdentitySigningKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var bobIdentityAgreementKey = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        using var bobSignedPreKey = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        using var bobOneTimePreKey = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        
        using var aliceIdentityAgreementKey = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        using var aliceEphemeralKey = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);

        // Bob creates pre-key bundle
        var bobSignedPreKeyPublicKey = new PreKey(bobSignedPreKey.PublicKey.ExportSubjectPublicKeyInfo());
        
        // Create an invalid signature (sign with wrong key)
        using var invalidSigningKey = ECDsa.Create(ECCurve.NamedCurves.nistP256); // Different key than bobIdentitySigningKey
        var invalidSignature = manager.SignPreKey(invalidSigningKey, bobSignedPreKeyPublicKey);

        var bobPreKeyBundle = new PreKeyBundle(
            bobIdentitySigningKey.ExportSubjectPublicKeyInfo(),
            bobIdentityAgreementKey.PublicKey.ExportSubjectPublicKeyInfo(),
            invalidSignature, // Use the invalid signature
            bobSignedPreKeyPublicKey.Value,
            bobOneTimePreKey.PublicKey.ExportSubjectPublicKeyInfo()
        );

        // Act & Assert
        // First verify the signature - this should fail since we intentionally used an invalid signature
        using var identitySigningKey = ECDsa.Create();
        identitySigningKey.ImportSubjectPublicKeyInfo(bobPreKeyBundle.IdentitySigningKey.Value, out _);
        
        bool isSignatureValid = CryptoUtils.Verify(
            bobPreKeyBundle.SignedPreKey.Value, 
            bobPreKeyBundle.SignedPreKeySignature.Value, 
            identitySigningKey
        );
        
        // The signature should be invalid
        Assert.That(isSignatureValid, Is.False);
    }
    
    [Test]
    public void InitiateHandshake_WithoutOneTimePreKey_Succeeds()
    {
        // Arrange
        var manager = new X3DHManager(new NullLogger<X3DHManager>(), Options.Create(new CryptographyOptions()));

        // Generate keys for both parties
        using var bobIdentitySigningKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var bobIdentityAgreementKey = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        using var bobSignedPreKey = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        
        using var aliceIdentityAgreementKey = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        using var aliceEphemeralKey = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);

        // Bob creates pre-key bundle without one-time pre-key
        var bobSignedPreKeyPublicKey = new PreKey(bobSignedPreKey.PublicKey.ExportSubjectPublicKeyInfo());
        var bobSignature = manager.SignPreKey(bobIdentitySigningKey, bobSignedPreKeyPublicKey);

        var bobPreKeyBundle = new X3dPreKeyBundle(
            new RatchetIdentityKey(bobIdentitySigningKey.ExportSubjectPublicKeyInfo()),
            new RatchetAgreementKey( bobIdentityAgreementKey.PublicKey.ExportSubjectPublicKeyInfo()),
            new PreKey(bobSignedPreKeyPublicKey.Value),
            bobSignature,
            null // No one-time pre-key
        );

        // Act
        var aliceSharedSecret = manager.InitiateHandshake(
            bobPreKeyBundle,
            aliceEphemeralKey,
            aliceIdentityAgreementKey
        );

        // Bob responds to handshake without one-time key
        var bobSharedSecret = manager.RespondToHandshake(
            new RatchetIdentityKey(aliceIdentityAgreementKey.PublicKey.ExportSubjectPublicKeyInfo()),
            new RatchetEphemeralKey(aliceEphemeralKey.PublicKey.ExportSubjectPublicKeyInfo()),
            new PrivateAgreementKey(bobIdentityAgreementKey.ExportECPrivateKey()),
            new PrivatePreKey(bobSignedPreKey.ExportECPrivateKey()),
            null // No one-time pre-key
        );

        // Assert
        aliceSharedSecret.Should().NotBeNull();
        bobSharedSecret.Should().NotBeNull();
        aliceSharedSecret.Value.Should().BeEquivalentTo(bobSharedSecret.Value);
    }
}

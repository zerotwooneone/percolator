using System.Security.Cryptography;
using FluentAssertions;
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
        using var aliceIdentitySigningKey = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        using var aliceIdentityAgreementKey = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        using var aliceEphemeralKey = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);

        // Bob (responder)
        using var bobIdentitySigningKey = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        using var bobSignedPreKey = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        using var bobOneTimePreKey = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);

        // --- Bob creates his pre-key bundle ---
        var bobSignedPreKeyPublicKey = new PreKey(bobSignedPreKey.PublicKey.ExportSubjectPublicKeyInfo());
        
        var bobPreKeyBundle = new X3dPreKeyBundle(
            new RatchetIdentityKey(bobIdentitySigningKey.ExportSubjectPublicKeyInfo()),
            new PreKey(bobSignedPreKeyPublicKey.Value),
            new OneTimeKey(bobOneTimePreKey.ExportSubjectPublicKeyInfo())
            );

        // --- Alice initiates the handshake ---
        // Alice receives Bob's bundle and initiates the handshake
        var aliceSharedSecret = manager.InitiateHandshake(
            bobPreKeyBundle,
            aliceEphemeralKey,
            aliceIdentitySigningKey
            );

        // --- Bob responds to the handshake ---
        // Bob receives Alice's identity and ephemeral keys
        var bobSharedSecret = manager.RespondToHandshake(
            new RatchetIdentityKey(aliceIdentitySigningKey.PublicKey.ExportSubjectPublicKeyInfo()),
            new RatchetEphemeralKey(aliceEphemeralKey.PublicKey.ExportSubjectPublicKeyInfo()),
            new RatchetIdentityKey(bobIdentitySigningKey.ExportECPrivateKey()),
            new PrivatePreKey(bobSignedPreKey.ExportECPrivateKey()),
            new PrivateOneTimeKey(bobOneTimePreKey.ExportECPrivateKey())
            );

        // Assert
        aliceSharedSecret.Should().NotBeNull();
        bobSharedSecret.Should().NotBeNull();
        aliceSharedSecret.Value.Should().BeEquivalentTo(bobSharedSecret.Value);
    }

    [Test]
    public void SignatureVerification_WithValidAndInvalidKeys_ReturnsCorrectResult()
    {
        // Arrange
        var manager = new X3DHManager(new NullLogger<X3DHManager>(), Options.Create(new CryptographyOptions()));
        using var identitySigningKey = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        using var otherSigningKey = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        using var preKey = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var preKeyToSign = new PreKey(preKey.PublicKey.ExportSubjectPublicKeyInfo());

        // Act
        var signature = manager.SignPreKey(identitySigningKey, preKeyToSign);
        var isSignatureValid = manager.VerifySignature(
            new RatchetIdentityKey(identitySigningKey.PublicKey.ExportSubjectPublicKeyInfo()), 
            preKeyToSign, 
            signature);
        var isSignatureInvalid = manager.VerifySignature(
            new RatchetIdentityKey(otherSigningKey.PublicKey.ExportSubjectPublicKeyInfo()), 
            preKeyToSign, 
            signature);

        // Assert
        isSignatureValid.Should().BeTrue();
        isSignatureInvalid.Should().BeFalse();
    }
    
    [Test]
    public void InitiateHandshake_WithoutOneTimePreKey_Succeeds()
    {
        // Arrange
        var manager = new X3DHManager(new NullLogger<X3DHManager>(), Options.Create(new CryptographyOptions()));

        // Generate keys for both parties
        using var aliceIdentitySigningKey = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        using var aliceEphemeralKey = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        
        using var bobIdentitySigningKey = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        using var bobSignedPreKey = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);

        // Bob creates pre-key bundle without one-time pre-key
        var bobSignedPreKeyPublicKey = new PreKey(bobSignedPreKey.PublicKey.ExportSubjectPublicKeyInfo());
        
        var bobPreKeyBundle = new X3dPreKeyBundle(
            new RatchetIdentityKey(bobIdentitySigningKey.ExportSubjectPublicKeyInfo()),
            new PreKey(bobSignedPreKeyPublicKey.Value),
            null // No one-time pre-key
        );

        // Act
        var aliceSharedSecret = manager.InitiateHandshake(
            bobPreKeyBundle,
            aliceEphemeralKey,
            aliceIdentitySigningKey
        );

        // Bob responds to handshake without one-time key
        var bobSharedSecret = manager.RespondToHandshake(
            new RatchetIdentityKey(aliceIdentitySigningKey.PublicKey.ExportSubjectPublicKeyInfo()),
            new RatchetEphemeralKey(aliceEphemeralKey.PublicKey.ExportSubjectPublicKeyInfo()),
            new RatchetIdentityKey(bobIdentitySigningKey.ExportECPrivateKey()),
            new PrivatePreKey(bobSignedPreKey.ExportECPrivateKey()),
            null // No one-time pre-key
        );

        // Assert
        aliceSharedSecret.Should().NotBeNull();
        bobSharedSecret.Should().NotBeNull();
        aliceSharedSecret.Value.Should().BeEquivalentTo(bobSharedSecret.Value);
    }
}

using System.Security.Cryptography;
using FluentAssertions;
using Percolator.Cryptography;

namespace Percolator.CryptographyTests;

[TestFixture]
public class X3DHManagerTests
{
    [Test]
    public void FullHandshake_Succeeds()
    {
        // Arrange
        var manager = new X3DHManager();

        // Alice's keys
        var ikA_signing = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var ikA_agreement = ECDiffieHellman.Create(ikA_signing.ExportParameters(true));

        // Bob's keys
        var ikB_signing = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var ikB_agreement = ECDiffieHellman.Create(ikB_signing.ExportParameters(true));
        var spkB = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var opkB = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);

        var spkB_bytes = spkB.PublicKey.ExportSubjectPublicKeyInfo();
        var signature = ikB_signing.SignData(spkB_bytes, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);

        var bundle = new PreKeyBundle(
            ikB_signing.ExportSubjectPublicKeyInfo(),
            spkB_bytes,
            signature,
            opkB.PublicKey.ExportSubjectPublicKeyInfo());

        // Act
        var aliceResult = manager.InitiateHandshake(bundle, ikA_signing, ikA_agreement);
        var sharedKeyBob = manager.RespondToHandshake(
            ikA_signing.ExportSubjectPublicKeyInfo(),
            aliceResult.EphemeralPublicKey.Value,
            ikB_signing,
            ikB_agreement,
            spkB,
            opkB);

        // Assert
        aliceResult.SharedSecret.Should().NotBeNull();
        aliceResult.SharedSecret.Value.Length.Should().Be(32);
        sharedKeyBob.Should().NotBeNull();
        aliceResult.SharedSecret.Value.Should().BeEquivalentTo(sharedKeyBob.Value);
    }

    [Test]
    public void InitiateHandshake_WithInvalidSignature_ThrowsException()
    {
        // Arrange
        var manager = new X3DHManager();
        var ikA_signing = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var ikA_agreement = ECDiffieHellman.Create(ikA_signing.ExportParameters(true));

        var ikB_signing = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var spkB = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var opkB = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);

        var spkB_bytes = spkB.PublicKey.ExportSubjectPublicKeyInfo();

        var bundle = new PreKeyBundle(
            ikB_signing.ExportSubjectPublicKeyInfo(),
            spkB_bytes,
            new byte[64], // Invalid signature
            opkB.PublicKey.ExportSubjectPublicKeyInfo()
        );

        // Act
        Action act = () => manager.InitiateHandshake(bundle, ikA_signing, ikA_agreement);
        
        // Assert
        act.Should().Throw<CryptographicException>().WithMessage("Invalid signature for signed pre-key.");
    }
}

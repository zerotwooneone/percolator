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
        var ekA = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);

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
        var sharedKeyAlice = manager.InitiateHandshake(bundle, ikA_signing, ikA_agreement, ekA);
        var sharedKeyBob = manager.RespondToHandshake(
            ikA_signing.ExportSubjectPublicKeyInfo(),
            ekA.PublicKey.ExportSubjectPublicKeyInfo(),
            ikB_signing,
            ikB_agreement,
            spkB,
            opkB);

        // Assert
        sharedKeyAlice.Should().NotBeNull();
        sharedKeyAlice.Length.Should().Be(32);
        sharedKeyAlice.Should().BeEquivalentTo(sharedKeyBob);
    }

    [Test]
    public void InitiateHandshake_WithInvalidSignature_ThrowsException()
    {
        var manager = new X3DHManager();
        var ikA_signing = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var ikA_agreement = ECDiffieHellman.Create(ikA_signing.ExportParameters(true));
        var ekA = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);

        var ikB_signing = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var spkB = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var opkB = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);

        var spkB_bytes = spkB.PublicKey.ExportSubjectPublicKeyInfo();
        var signature = ikB_signing.SignData(spkB_bytes, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);

        var bundle = new PreKeyBundle(
            ikB_signing.ExportSubjectPublicKeyInfo(),
            spkB.PublicKey.ExportSubjectPublicKeyInfo(),
            new byte[64], // Invalid signature
            opkB.PublicKey.ExportSubjectPublicKeyInfo()
        );

        Action act = () => manager.InitiateHandshake(bundle, ikA_signing, ikA_agreement, ekA);
        act.Should().Throw<CryptographicException>().WithMessage("Invalid signature for signed pre-key.");
    }
}

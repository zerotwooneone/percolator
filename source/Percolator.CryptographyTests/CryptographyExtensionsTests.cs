using System.Security.Cryptography;
using FluentAssertions;
using Percolator.Cryptography;

namespace Percolator.CryptographyTests;

[TestFixture]
public class CryptographyExtensionsTests
{
    [Test]
    public void ToEcdhRatchetIdentityKey_ShouldReturnCorrectPublicKey()
    {
        // Arrange
        using var ecdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var originalPublicKeyBytes = ecdh.PublicKey.ExportSubjectPublicKeyInfo();
        var publicKey = new RatchetIdentityKey(originalPublicKeyBytes);

        // Act
        var result = publicKey.ToEcdhPublicKey();

        // Assert
        result.Should().NotBeNull();
        var resultBytes = result.ExportSubjectPublicKeyInfo();
        resultBytes.Should().BeEquivalentTo(originalPublicKeyBytes);
    }
}

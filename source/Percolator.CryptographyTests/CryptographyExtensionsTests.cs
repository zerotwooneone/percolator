using NUnit.Framework;
using System.Security.Cryptography;
using FluentAssertions;

namespace Percolator.Cryptography.Tests;

[TestFixture]
public class CryptographyExtensionsTests
{
    [Test]
    public void ToEcdhPublicKey_ShouldReturnCorrectPublicKey()
    {
        // Arrange
        using var ecdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var originalPublicKeyBytes = ecdh.PublicKey.ExportSubjectPublicKeyInfo();
        var publicKey = new PublicKey(originalPublicKeyBytes);

        // Act
        var result = publicKey.ToEcdhPublicKey();

        // Assert
        result.Should().NotBeNull();
        var resultBytes = result.ExportSubjectPublicKeyInfo();
        resultBytes.Should().BeEquivalentTo(originalPublicKeyBytes);
    }
}

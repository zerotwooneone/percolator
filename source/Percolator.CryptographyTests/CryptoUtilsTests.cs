using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using Percolator.Cryptography;

namespace Percolator.CryptographyTests;

[TestFixture]
public class CryptoUtilsTests
{
    private byte[] _key = null!;
    private byte[] _plaintext = null!;
    private byte[] _associatedData = null!;
    private ECDsa _signingKey = null!;
    private ECDsa _verifyingKey = null!;


    [SetUp]
    public void Setup()
    {
        _key = RandomNumberGenerator.GetBytes(32);
        _plaintext = Encoding.UTF8.GetBytes("This is a super secret message.");
        _associatedData = Encoding.UTF8.GetBytes("metadata");
        _signingKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        _verifyingKey = ECDsa.Create(_signingKey.ExportParameters(false));
    }

    [TearDown]
    public void Teardown()
    {
        _signingKey.Dispose();
        _verifyingKey.Dispose();
    }

    [Test]
    public void EncryptDecryptAesGcm_ShouldRoundtripSuccessfully()
    {
        // Act
        var ciphertext = CryptoUtils.EncryptAesGcm(_key, 1, _plaintext, _associatedData);
        var decrypted = CryptoUtils.DecryptAesGcm(_key, 1, ciphertext, _associatedData);

        // Assert
        decrypted.Should().Equal(_plaintext);
    }

    [Test]
    public void DecryptAesGcm_WithTamperedCiphertext_ShouldThrow()
    {
        // Arrange
        var ciphertext = CryptoUtils.EncryptAesGcm(_key, 1, _plaintext, _associatedData);
        ciphertext[0] ^= 0xff; // Tamper with the first byte

        // Act
        Action act = () => CryptoUtils.DecryptAesGcm(_key, 1, ciphertext, _associatedData);

        // Assert
        act.Should().Throw<CryptographicException>();
    }

    [Test]
    public void DecryptAesGcm_WithTamperedAssociatedData_ShouldThrow()
    {
        // Arrange
        var ciphertext = CryptoUtils.EncryptAesGcm(_key, 1, _plaintext, _associatedData);
        var tamperedAssociatedData = Encoding.UTF8.GetBytes("tampered-metadata");

        // Act
        Action act = () => CryptoUtils.DecryptAesGcm(_key, 1, ciphertext, tamperedAssociatedData);

        // Assert
        act.Should().Throw<CryptographicException>();
    }

    [Test]
    public void EncryptDecryptAtRest_ShouldRoundtripSuccessfully()
    {
        // Act
        var encryptedPayload = CryptoUtils.EncryptAtRest(_key, _plaintext, _associatedData);
        var decrypted = CryptoUtils.DecryptAtRest(_key, encryptedPayload, _associatedData);

        // Assert
        decrypted.Should().Equal(_plaintext);
    }

    [Test]
    public void DecryptAtRest_WithTamperedPayload_ShouldThrow()
    {
        // Arrange
        var encryptedPayload = CryptoUtils.EncryptAtRest(_key, _plaintext, _associatedData);
        encryptedPayload[20] ^= 0xff; // Tamper with a byte in the middle (likely ciphertext or tag)

        // Act
        Action act = () => CryptoUtils.DecryptAtRest(_key, encryptedPayload, _associatedData);

        // Assert
        act.Should().Throw<CryptographicException>();
    }

    [Test]
    public void SignVerify_ShouldRoundtripSuccessfully()
    {
        // Act
        var signature = CryptoUtils.Sign(_plaintext, _signingKey);
        var isValid = CryptoUtils.Verify(_plaintext, signature, _verifyingKey);

        // Assert
        isValid.Should().BeTrue();
    }

    [Test]
    public void Verify_WithTamperedData_ShouldFail()
    {
        // Arrange
        var signature = CryptoUtils.Sign(_plaintext, _signingKey);
        var tamperedPlaintext = Encoding.UTF8.GetBytes("This is NOT the secret message.");

        // Act
        var isValid = CryptoUtils.Verify(tamperedPlaintext, signature, _verifyingKey);

        // Assert
        isValid.Should().BeFalse();
    }

    [Test]
    public void Verify_WithTamperedSignature_ShouldFail()
    {
        // Arrange
        var signature = CryptoUtils.Sign(_plaintext, _signingKey);
        signature[5] ^= 0xff; // Tamper with signature

        // Act
        var isValid = CryptoUtils.Verify(_plaintext, signature, _verifyingKey);

        // Assert
        isValid.Should().BeFalse();
    }
}

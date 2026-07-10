using Percolator.Cryptography;

namespace Percolator.CryptographyTests;

[TestFixture]
public class ProfileCryptoRoundTripTests
{
    [Test]
    public void ProfileCryptoRoundTrip_EncryptThenDecrypt_YieldsOriginalPlaintext()
    {
        // Arrange
        var secureRandom = new SystemSecureRandom();
        var cryptoService = new ProfileCryptographyService();
        
        var originalPlaintext = "Alice Johnson";
        var plaintextBytes = System.Text.Encoding.UTF8.GetBytes(originalPlaintext);
        var plaintext = ProfilePlaintextBytes.FromBytesOwned(plaintextBytes);
        
        var keyBytes = secureRandom.GetBytes(32);
        var key = ProfileKeyBytes.FromBytesOwned(keyBytes);

        // Act
        var encryptionResult = cryptoService.EncryptData(plaintext, key);
        var decryptedPlaintext = cryptoService.DecryptData(
            encryptionResult.Ciphertext, 
            encryptionResult.Nonce, 
            encryptionResult.Tag, 
            key);

        // Assert
        var decryptedString = System.Text.Encoding.UTF8.GetString(decryptedPlaintext.Span.ToArray());
        Assert.That(decryptedString, Is.EqualTo(originalPlaintext));
    }
}

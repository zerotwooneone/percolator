using FluentAssertions;
using Percolator.Cryptography;
using Percolator.Infrastructure.Cryptography;

namespace Percolator.InfrastructureTests.Cryptography;

[TestFixture]
public class NativeEd25519CryptographyServiceTests
{
    private readonly NativeEd25519CryptographyService _service = new();

    [Test]
    public void GenerateKeyPair_CreatesValidKeys()
    {
        // ACT
        _service.GenerateKeyPair(out var privateKey, out var publicKey);

        // ASSERT
        privateKey.Should().NotBeNull();
        publicKey.Should().NotBeNull();
        privateKey.Span.Length.Should().Be(32);
        publicKey.Span.Length.Should().Be(32);
        privateKey.Span.ToArray().Should().NotBeNullOrEmpty();
        publicKey.Span.ToArray().Should().NotBeNullOrEmpty();
        // Verify keys are not all zeros by checking at least one byte is non-zero
        privateKey.Span.ToArray().Any(b => b != 0).Should().BeTrue();
        publicKey.Span.ToArray().Any(b => b != 0).Should().BeTrue();
    }

    [Test]
    public void SignAndVerify_RoundTripsSuccessfully()
    {
        // ARRANGE
        _service.GenerateKeyPair(out var privateKey, out var publicKey);
        var message = "Hello, Ed25519!"u8.ToArray();

        // ACT
        var signature = _service.Sign(message, privateKey);
        var isValid = _service.Verify(publicKey, message, signature);

        // ASSERT
        isValid.Should().BeTrue();
    }

    [Test]
    public void Verify_ReturnsFalse_ForInvalidSignature()
    {
        // ARRANGE
        _service.GenerateKeyPair(out var privateKey, out var publicKey);
        var message = "Hello, Ed25519!"u8.ToArray();
        var signature = _service.Sign(message, privateKey);

        // Tamper with the signature
        var tamperedSignature = Ed25519SignatureBytes.FromBytesOwned(signature.Span.ToArray());
        var tamperedBytes = tamperedSignature.Span.ToArray();
        tamperedBytes[0] ^= 0xFF;
        tamperedSignature = Ed25519SignatureBytes.FromBytesOwned(tamperedBytes);

        // ACT
        var isValid = _service.Verify(publicKey, message, tamperedSignature);

        // ASSERT
        isValid.Should().BeFalse();
    }

    [Test]
    public void SignAndVerify_ReturnsFalse_ForWrongMessage()
    {
        // ARRANGE
        _service.GenerateKeyPair(out var privateKey, out var publicKey);
        var message = "Hello, Ed25519!"u8.ToArray();
        var signature = _service.Sign(message, privateKey);
        var wrongMessage = "Wrong message"u8.ToArray();

        // ACT
        var isValid = _service.Verify(publicKey, wrongMessage, signature);

        // ASSERT
        isValid.Should().BeFalse();
    }
}

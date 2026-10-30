namespace Percolator.Infrastructure.Tests.Persistence.Core;

using System;
using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using NUnit.Framework;
using Percolator.Infrastructure.Persistence.Converters;
using Percolator.Infrastructure.Vault;

[TestFixture]
public class AesGcmValueConverterTests
{
    private MasterKeyHandle _handle = null!;

    [SetUp]
    public void SetUp()
    {
        _handle = MasterKeyHandle.GenerateRandom();
    }

    [TearDown]
    public void TearDown()
    {
        _handle.Dispose();
    }

    [Test]
    public void EncryptAndDecrypt_RoundtripsSuccessfully()
    {
        // Arrange
        var converter = new AesGcmValueConverter(_handle);
        byte[] plaintext = Encoding.UTF8.GetBytes("SuperSecretPayload-12345");

        // Act - ConvertToProvider (Encrypt)
        var encrypted = (byte[]?)converter.ConvertToProvider(plaintext);

        // Assert encrypted format
        encrypted.Should().NotBeNull();
        // Layout: 12-byte Nonce + 16-byte Tag + N-byte Ciphertext
        encrypted!.Length.Should().Be(12 + 16 + plaintext.Length);

        // Act - ConvertFromProvider (Decrypt)
        var decrypted = (byte[]?)converter.ConvertFromProvider(encrypted);

        // Assert roundtrip
        decrypted.Should().NotBeNull();
        decrypted.Should().Equal(plaintext);
    }

    [Test]
    public void NullableConverter_HandlesNullAndEmpty()
    {
        var converter = new NullableAesGcmValueConverter(_handle);

        converter.ConvertToProvider(null).Should().BeNull();
        converter.ConvertFromProvider(null).Should().BeNull();

        var emptyEncrypted = (byte[]?)converter.ConvertToProvider(Array.Empty<byte>());
        emptyEncrypted.Should().NotBeNull();
        emptyEncrypted!.Length.Should().Be(0);
    }
}

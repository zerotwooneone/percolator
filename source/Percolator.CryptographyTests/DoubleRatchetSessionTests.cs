using NUnit.Framework;
using FluentAssertions;
using System;
using Pecolator.Cryptography;

namespace Percolator.CryptographyTests
{
    [TestFixture]
    public class DoubleRatchetSessionTests
    {
        [Test]
        public void Constructor_WhenCalled_ShouldNotThrow()
        {
            // Arrange

            // Act
            Action act = () => new DoubleRatchetSession();

            // Assert
            act.Should().NotThrow();
        }

        [Test]
        public void Encrypt_WithValidPlaintext_ReturnsNonEmptyCiphertext()
        {
            // Arrange
            var session = new DoubleRatchetSession();
            var plaintext = System.Text.Encoding.UTF8.GetBytes("Hello, world!");

            // Act
            var ciphertext = session.Encrypt(plaintext);

            // Assert
            ciphertext.Should().NotBeNull();
            ciphertext.Should().NotBeEmpty();
        }

        [Test]
        public void Encrypt_CalledTwiceWithSamePlaintext_ReturnsDifferentCiphertexts()
        {
            // Arrange
            var session = new DoubleRatchetSession();
            var plaintext = System.Text.Encoding.UTF8.GetBytes("You can't step in the same river twice.");

            // Act
            var ciphertext1 = session.Encrypt(plaintext);
            var ciphertext2 = session.Encrypt(plaintext);

            // Assert
            ciphertext1.Should().NotBeEquivalentTo(ciphertext2);
        }
    }
}

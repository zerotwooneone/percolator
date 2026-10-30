namespace Percolator.Infrastructure.Tests.Vault.Keying;

using System.Security.Cryptography;
using Percolator.Infrastructure.Vault;

[TestFixture]
public class MasterKeyHandleTests
{
    [Test]
    public void WithKeySpan_YieldsValid32ByteKeySpan()
    {
        // Arrange
        byte[] expectedKey = RandomNumberGenerator.GetBytes(32);
        using var handle = new MasterKeyHandle(expectedKey);

        // Act & Assert Action visitor
        handle.WithKeySpan(keySpan =>
        {
            keySpan.Length.Should().Be(32);
            keySpan.SequenceEqual(expectedKey).Should().BeTrue();
        });

        // Act & Assert Func visitor
        bool matches = handle.WithKeySpan(keySpan => keySpan.SequenceEqual(expectedKey));
        matches.Should().BeTrue();
    }

    [Test]
    public void Dispose_ZeroizesPinnedMemory()
    {
        // Arrange
        byte[] expectedKey = RandomNumberGenerator.GetBytes(32);
        var handle = new MasterKeyHandle(expectedKey);

        // Act
        handle.Dispose();

        // Assert: Subsequent access throws ObjectDisposedException
        Action accessAction = () => handle.WithKeySpan(span => { });
        accessAction.Should().Throw<ObjectDisposedException>();

        Action funcAction = () => handle.WithKeySpan(span => span.Length);
        funcAction.Should().Throw<ObjectDisposedException>();
    }

    [Test]
    public void InvalidKeyLength_ThrowsArgumentException()
    {
        byte[] invalidKey = new byte[16];
        Action act = () => new MasterKeyHandle(invalidKey);
        act.Should().Throw<ArgumentException>();
    }
}

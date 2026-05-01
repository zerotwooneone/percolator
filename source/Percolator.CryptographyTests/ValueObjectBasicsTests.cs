using FluentAssertions;
using Percolator.Cryptography;

namespace Percolator.CryptographyTests;

[TestFixture]
public class ValueObjectBasicsTests
{
    [Test]
    public void SessionId_NewId_IsNotEmpty_And_ToString_NotEmpty()
    {
        var id = SessionId.NewId();
        id.Value.Should().NotBe(Guid.Empty);
        id.ToString().Should().NotBeNullOrWhiteSpace();
    }

    [Test]
    public void SessionId_Empty_Throws()
    {
        Action act = () => _ = new SessionId(Guid.Empty);
        act.Should().Throw<ArgumentException>();
    }

    [Test]
    public void PendingSessionId_NewId_IsNotEmpty()
    {
        var id = PendingSessionId.NewId();
        id.Value.Should().NotBe(Guid.Empty);
    }

    [Test]
    public void PendingSessionId_Empty_Throws()
    {
        Action act = () => _ = new PendingSessionId(Guid.Empty);
        act.Should().Throw<ArgumentException>();
    }

    [Test]
    public void Plaintext_Equality_IsByContent()
    {
        var a1 = Plaintext.FromBytes(new byte[] {1,2,3});
        var a2 = Plaintext.FromBytes(new byte[] {1,2,3});
        var b = Plaintext.FromBytes(new byte[] {1,2,4});
        a1.Should().Be(a2);
        a1.Should().NotBe(b);
    }

    [Test]
    public void Ciphertext_Equality_IsByContent()
    {
        var a1 = Ciphertext.FromBytes(new byte[] {9,8,7});
        var a2 = Ciphertext.FromBytes(new byte[] {9,8,7});
        var b = Ciphertext.FromBytes(new byte[] {9,8,6});
        a1.Should().Be(a2);
        a1.Should().NotBe(b);
    }

    [Test]
    public void AssociatedData_Equality_IsByContent()
    {
        var a1 = AssociatedData.FromBytes(new byte[] {5,5,5});
        var a2 = AssociatedData.FromBytes(new byte[] {5,5,5});
        var b = AssociatedData.FromBytes(new byte[] {5,5,6});
        a1.Should().Be(a2);
        a1.Should().NotBe(b);
    }
}

using System;
using FluentAssertions;
using NUnit.Framework;
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
        var a1 = new Plaintext(new byte[] {1,2,3});
        var a2 = new Plaintext(new byte[] {1,2,3});
        var b = new Plaintext(new byte[] {1,2,4});
        a1.Should().Be(a2);
        a1.Should().NotBe(b);
    }

    [Test]
    public void Ciphertext_Equality_IsByContent()
    {
        var a1 = new Ciphertext(new byte[] {9,8,7});
        var a2 = new Ciphertext(new byte[] {9,8,7});
        var b = new Ciphertext(new byte[] {9,8,6});
        a1.Should().Be(a2);
        a1.Should().NotBe(b);
    }

    [Test]
    public void AssociatedData_Equality_IsByContent()
    {
        var a1 = new AssociatedData(new byte[] {5,5,5});
        var a2 = new AssociatedData(new byte[] {5,5,5});
        var b = new AssociatedData(new byte[] {5,5,6});
        a1.Should().Be(a2);
        a1.Should().NotBe(b);
    }
}

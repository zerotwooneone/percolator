using System;
using FluentAssertions;
using NUnit.Framework;
using Percolator.Cryptography;
using Percolator.Cryptography.Primitives;

namespace Percolator.CryptographyTests;

file sealed class TestClock10 : IClock
{
    public DateTimeOffset UtcNow { get; set; } = DateTimeOffset.Parse("2025-08-05T00:00:00Z");
}

[TestFixture]
public class SecureSessionDhAsymmetryTests
{
    [Test]
    public void Initiator_First_Encrypt_Uses_NonTrivial_HeaderKey()
    {
        var clock = new TestClock10();
        var s = SecureSession.Create(
            SessionId.NewId(),
            PeerId.NewId(),
            new ProtocolVersion(1),
            new RatchetState(new RootKey(new byte[32]), null, 0, null, 0, 0, null, null, 1000),
            clock);

        var msg = s.Encrypt(new Plaintext(new byte[] { 0x01 }), clock);
        var (preKey, _, _) = msg.GetHeader();

        preKey.Value.Should().NotBeNull();
        preKey.Value.Length.Should().BeGreaterThanOrEqualTo(32); // expect real DH pubkey length
        preKey.Value[0].Should().NotBe(0x01); // not the trivial placeholder key
    }
}

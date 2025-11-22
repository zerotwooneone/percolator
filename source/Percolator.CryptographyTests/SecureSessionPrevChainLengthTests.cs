using System;
using FluentAssertions;
using NUnit.Framework;
using Percolator.Cryptography;
using Percolator.Cryptography.Primitives;

namespace Percolator.CryptographyTests;

file sealed class TestClock9 : IClock
{
    public DateTimeOffset UtcNow { get; set; } = DateTimeOffset.Parse("2025-08-04T00:00:00Z");
}

[TestFixture]
public class SecureSessionPrevChainLengthTests
{
    [Test]
    public void Second_Encrypt_Sets_PrevChainLength_To_Previous_Send_Count()
    {
        var clock = new TestClock9();
        var crypto = new AeadSessionCrypto();
        var s = SecureSession.Create(
            SessionId.NewId(),
            PeerId.NewId(),
            new ProtocolVersion(1),
            new RatchetState(new RootKey(new byte[32]), null, 0, null, 0, 0, null, null, 1000),
            crypto,
            clock);

        var first = s.Encrypt(new Plaintext(new byte[] { 1 }), clock);
        var second = s.Encrypt(new Plaintext(new byte[] { 2 }), clock);

        var (_, _, prevLenFirst) = first.GetHeader();
        var (_, _, prevLenSecond) = second.GetHeader();

        prevLenFirst.Should().Be(0UL);
        prevLenSecond.Should().Be(1UL); // Expect previous chain length to reflect prior send count
    }
}

using System;
using FluentAssertions;
using NUnit.Framework;
using Percolator.Cryptography;
using Percolator.Cryptography.Primitives;

namespace Percolator.CryptographyTests;

file sealed class TestClock3 : IClock
{
    public DateTimeOffset UtcNow { get; set; } = DateTimeOffset.Parse("2025-06-01T10:00:00Z");
}

[TestFixture]
public class SecureSessionApiSurfaceTests
{
    [Test]
    public void Encrypt_And_Decrypt_Methods_Are_Invokable_And_Return_Results()
    {
        var clock = new TestClock3();
        var crypto = new AeadSessionCrypto();
        var s = SecureSession.Create(
            SessionId.NewId(),
            PeerId.NewId(),
            new ProtocolVersion(1),
            new RatchetState(new RootKey(new byte[32]), null, 0, null, 0, 0, null, null, 1000),
            crypto,
            clock);

        var encrypted = s.Encrypt(new Plaintext(new byte[] { 1 }), clock);
        encrypted.Should().NotBeNull();
        encrypted.GetCiphertext().Value.Should().NotBeNull();

        // Decrypt a real message produced by the session (sanity)
        var decrypted = s.Decrypt(encrypted, clock);
        decrypted.Value.Should().NotBeNull();
    }

    [Test]
    public void TouchLastUsed_Updates_Timestamp()
    {
        var clock = new TestClock3();
        var crypto = new AeadSessionCrypto();
        var s = SecureSession.Create(
            SessionId.NewId(),
            PeerId.NewId(),
            new ProtocolVersion(1),
            new RatchetState(new RootKey(new byte[32]), null, 0, null, 0, 0, null, null, 1000),
            crypto,
            clock);

        var created = s.CreatedAtUtc;
        s.LastUsedAtUtc.Should().Be(created);

        clock.UtcNow = clock.UtcNow.AddMinutes(5);
        s.TouchLastUsed(clock);
        s.LastUsedAtUtc.Should().Be(clock.UtcNow);
    }
}

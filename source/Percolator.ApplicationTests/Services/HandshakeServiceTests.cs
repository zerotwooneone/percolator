using System;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using NUnit.Framework;
using Percolator.Application.Services;
using Percolator.Cryptography;
using Percolator.Cryptography.Primitives;

namespace Percolator.ApplicationTests.Services;

[TestFixture]
public class HandshakeServiceTests
{
    [Test]
    public async Task InitiateStandardHandshake_returns_session_and_initial_cipher_when_plaintext_provided()
    {
        var svc = new HandshakeService();
        var result = await svc.InitiateStandardHandshakeAsync(new PeerId(Guid.NewGuid()), new Plaintext(new byte[]{0xAA}), CancellationToken.None);
        result.SessionId.Should().NotBeNull();
        result.InitialCipher.Should().NotBeNull();
    }

    [Test]
    public async Task InitiateStandardHandshake_initial_cipher_decrypts_on_complementary_responder()
    {
        // Arrange: create sender via HandshakeService (uses zeroed root and fixed init labels)
        var svc = new HandshakeService();
        var plaintext = new Plaintext(new byte[] { 0x10, 0x20, 0x30 });
        var compose = await svc.InitiateStandardHandshakeAsync(new PeerId(Guid.NewGuid()), plaintext, CancellationToken.None);
        Assert.That(compose.InitialCipher, Is.Not.Null);

        // Build complementary responder session using same root and swapped init labels
        var root = new RootKey(new byte[32]);
        var recvCk = new ChainKey(CryptoUtils.KDF(null, root.Value, "dr-send-init", CryptoUtils.KeySize));
        var sendCk = new ChainKey(CryptoUtils.KDF(null, root.Value, "dr-recv-init", CryptoUtils.KeySize));
        var responderState = new RatchetState(root, sendCk, 0, recvCk, 0, 0, null, null, 1000);
        var responder = SecureSession.Create(SessionId.NewId(), new PeerId(Guid.NewGuid()), new ProtocolVersion(1), responderState, new AeadSessionCrypto(), new TestClock());

        // Act: decrypt the sender's initial cipher
        var decrypted = responder.Decrypt(compose.InitialCipher!, new TestClock());

        // Assert
        Assert.That(decrypted.Value, Is.EqualTo(plaintext.Value));
    }

    private sealed class TestClock : IClock
    {
        public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
    }
}

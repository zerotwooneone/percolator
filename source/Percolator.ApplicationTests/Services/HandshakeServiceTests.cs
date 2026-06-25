using FluentAssertions;
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
        var svc = new HandshakeService(new TestClock());
        var result = await svc.InitiateStandardHandshakeAsync(new PeerId(Guid.NewGuid()), Plaintext.FromBytes(new byte[]{0xAA}), CancellationToken.None);
        result.SessionId.Should().NotBeNull();
        result.InitialCipher.Should().NotBeNull();
    }

    [Test]
    public async Task InitiateStandardHandshake_initial_cipher_decrypts_on_complementary_responder()
    {
        // Arrange: create sender via HandshakeService (uses zeroed root and fixed init labels)
        var svc = new HandshakeService(new TestClock());
        var plaintext = Plaintext.FromBytes(new byte[] { 0x10, 0x20, 0x30 });
        var compose = await svc.InitiateStandardHandshakeAsync(new PeerId(Guid.NewGuid()), plaintext, CancellationToken.None);
        Assert.That(compose.InitialCipher, Is.Not.Null);

        // Build complementary responder session using canonical bootstrap
        var root = RootKey.FromBytes(new byte[32]);
        var responder = RatchetBootstrap.CreateResponderSession(
            SessionId.NewId(),
            new PeerId(Guid.NewGuid()),
            new ProtocolVersion(1),
            root,
            new TestClock());

        // Act: decrypt the sender's initial cipher
        var decrypted = responder.Decrypt(compose.InitialCipher!, new TestClock());

        // Assert
        Assert.That(decrypted.ToArray(), Is.EqualTo(plaintext.ToArray()));
    }

    
}

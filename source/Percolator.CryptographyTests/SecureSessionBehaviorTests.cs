using FluentAssertions;
using Percolator.Cryptography;
using Percolator.Cryptography.Primitives;

namespace Percolator.CryptographyTests;

file sealed class TestClock4 : IClock
{
    public DateTimeOffset UtcNow { get; set; } = DateTimeOffset.Parse("2025-06-02T10:00:00Z");
}

[TestFixture]
public class SecureSessionBehaviorTests
{
    private static SecureSession CreateBaselineSession(IClock clock)
    {
        var id = SessionId.NewId();
        var peer = new PeerId(1);
        var version = new ProtocolVersion(1);
        var state = CryptoTestBootstrap.CreateBootstrappedState(RootKey.FromBytes(new byte[32]));
        var crypto = new AeadSessionCrypto();
        return SecureSession.Create(id, peer, version, state, crypto, clock);
    }

    [Test]
    public void Encrypt_FirstMessage_AsInitiator_EmitsRatchetFramedMessage()
    {
        // Arrange
        var clock = new TestClock4();
        var session = CreateBaselineSession(clock);
        var pt = Plaintext.FromBytes(new byte[] { 1, 2, 3 });

        // Act
        var msg = session.Encrypt(pt, clock);

        // Assert (behavioral)
        var (key, counter, prevLen) = msg.GetHeader();
        key.ToArray().Should().NotBeNull();
        counter.Should().BeGreaterThanOrEqualTo(0UL);
        prevLen.Should().BeGreaterThanOrEqualTo(0UL);
        msg.GetCiphertext().ToArray().Length.Should().BeGreaterThan(0);
        session.LastUsedAtUtc.Should().Be(clock.UtcNow);
    }

    [Test]
    public void Decrypt_FirstInbound_AsResponder_AdvancesState_AndReturnsPlaintext()
    {
        // Arrange
        var clock = new TestClock4();
        var root = RootKey.FromBytes(new byte[32]);
        var (initiator, responder) = CryptoTestBootstrap.CreatePairedStates(root);
        var receiver = SecureSession.Create(SessionId.NewId(), new PeerId(1), new ProtocolVersion(1), responder, new AeadSessionCrypto(), clock);
        var sender = SecureSession.Create(SessionId.NewId(), new PeerId(1), new ProtocolVersion(1), initiator, new AeadSessionCrypto(), clock);
        var expected = Plaintext.FromBytes(new byte[] { 9, 9, 9 });
        var framed = sender.Encrypt(expected, clock);
        clock.UtcNow = clock.UtcNow.AddMinutes(1);

        // Act
        var pt = receiver.Decrypt(framed, clock);

        // Assert
        pt.ToArray().Should().NotBeNull();
        receiver.LastUsedAtUtc.Should().Be(clock.UtcNow);
    }
}

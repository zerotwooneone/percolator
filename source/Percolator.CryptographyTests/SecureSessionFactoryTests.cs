using FluentAssertions;
using Moq;
using Percolator.Cryptography;
using Percolator.Cryptography.Primitives;

namespace Percolator.CryptographyTests;

file sealed class TestClock5 : IClock
{
    public DateTimeOffset UtcNow { get; set; } = DateTimeOffset.Parse("2025-07-01T00:00:00Z");
}


file sealed class DummySessionCrypto : ISessionCrypto
{
    public (SharedSecret SharedSecret, RatchetEphemeralKey EphemeralPublic) X3DH_Initiate(PrivatePreKey localIdentityPrivate, PreKeyBundle remoteBundle)
    {
        return (SharedSecret.FromBytes(new byte[32]), RatchetEphemeralKey.FromBytes(new byte[64]));
    }

    public (Ciphertext Ciphertext, RatchetEphemeralKey HeaderKey, RatchetState NewState) DR_Encrypt(
        RatchetState state,
        Plaintext pt,
        AssociatedData ad,
        ulong counter,
        ulong previousChainLength)
        => (Ciphertext.FromBytes(new byte[] { 0x01 }), RatchetEphemeralKey.FromBytes(new byte[64]), state);

    public (Plaintext Plaintext, RatchetState NewState) DR_Decrypt(RatchetState state, SessionRatchetMessage framed, AssociatedData ad)
        => (Plaintext.FromBytes(new byte[] { 0x01 }), state);

    public bool VerifySignature(RatchetIdentityKey identityPublic, PreKey signedPreKey, Signature signature) => true;

    public SharedSecret X3DH_Respond(
        RatchetIdentityKey initiatorIdentityPublic,
        RatchetEphemeralKey initiatorEphemeralPublic,
        PrivatePreKey responderIdentityPrivate,
        PrivatePreKey responderSignedPreKeyPrivate,
        PrivatePreKey? responderOneTimePreKeyPrivate)
        => SharedSecret.FromBytes(new byte[32]);
}

[TestFixture]
public class SecureSessionFactoryTests
{
    [Test]
    public void EstablishFromX3DH_AsInitiator_Creates_Session_With_Initial_State()
    {
        // Arrange
        var clock = new TestClock5();
        var peer = PeerId.NewId();
        var version = new ProtocolVersion(1);
        var bundle = new PreKeyBundle(
            RatchetIdentityKey.FromBytes(new byte[64]),
            Guid.NewGuid(),
            PreKey.FromBytes(new byte[64]),
            Signature.FromBytes(new byte[60]),
            Guid.NewGuid(),
            OneTimeKey.FromBytes(new byte[64]),
            DateTimeOffset.UtcNow.AddDays(1));

        var keyStore = new Mock<IKeyStore>(MockBehavior.Strict);
        keyStore.Setup(k => k.GetIdentityPrivateKey()).Returns(PrivatePreKey.FromBytes(new byte[100]));

        // Act
        var session = SecureSession.EstablishFromX3DH(bundle, keyStore.Object, new DummySessionCrypto(), peer, version, clock);

        // Assert
        session.Should().NotBeNull();
        session.RemotePeerId.Should().Be(peer);
        session.ProtocolVersion.Should().Be(version);
        session.CreatedAtUtc.Should().Be(clock.UtcNow);
        session.LastUsedAtUtc.Should().Be(clock.UtcNow);
        session.State.Should().NotBeNull();
    }
}

using System;
using System.Security.Cryptography;
using FluentAssertions;
using Moq;
using NUnit.Framework;
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
        return (new SharedSecret(new byte[32]), new RatchetEphemeralKey(new byte[] { 0xEE }));
    }

    public (Ciphertext Ciphertext, RatchetEphemeralKey HeaderKey, RatchetState NewState) DR_Encrypt(
        RatchetState state,
        Plaintext pt,
        AssociatedData ad,
        ulong counter,
        ulong previousChainLength)
        => (new Ciphertext(new byte[] { 0x01 }), new RatchetEphemeralKey(new byte[] { 0x02 }), state);

    public (Plaintext Plaintext, RatchetState NewState) DR_Decrypt(RatchetState state, SessionRatchetMessage framed, AssociatedData ad)
        => (new Plaintext(new byte[] { 0x01 }), state);

    public bool VerifySignature(RatchetIdentityKey identityPublic, PreKey signedPreKey, Signature signature) => true;

    public SharedSecret X3DH_Respond(
        RatchetIdentityKey initiatorIdentityPublic,
        RatchetEphemeralKey initiatorEphemeralPublic,
        PrivatePreKey responderIdentityPrivate,
        PrivatePreKey responderSignedPreKeyPrivate,
        PrivatePreKey? responderOneTimePreKeyPrivate)
        => new SharedSecret(new byte[32]);
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
            new RatchetIdentityKey(new byte[] { 0x10 }),
            Guid.NewGuid(),
            new PreKey(new byte[] { 0x20 }),
            new Signature(new byte[] { 0x30 }),
            Guid.NewGuid(),
            new OneTimeKey(new byte[] { 0x40 }),
            DateTimeOffset.UtcNow.AddDays(1));

        // Act
        var session = SecureSession.EstablishFromX3DH(bundle, new Mock<IKeyStore>().Object, new DummySessionCrypto(), peer, version, clock);

        // Assert
        session.Should().NotBeNull();
        session.RemotePeerId.Should().Be(peer);
        session.ProtocolVersion.Should().Be(version);
        session.CreatedAtUtc.Should().Be(clock.UtcNow);
        session.LastUsedAtUtc.Should().Be(clock.UtcNow);
        session.State.Should().NotBeNull();
    }
}

using NUnit.Framework;
using Percolator.Domain.Identities.Model;
using Percolator.Domain.Identities.ValueObjects;

namespace Percolator.Domain.Tests.Identities;

[TestFixture]
public sealed class KeyRotationTests
{
    private readonly PublicIdentityId _ownerId = PublicIdentityId.New();
    private readonly PublicIdentityId _peerId = PublicIdentityId.New();
    private readonly DateTimeOffset _fixedTime = new(2026, 9, 27, 0, 0, 0, TimeSpan.Zero);

    [Test]
    public void RotatePrimaryPublicKey_WhenPeerChangesKey_ResetsTrustToUntrusted()
    {
        // Arrange
        var initialKey = IdentityKey.FromSpan(new byte[32]);
        var contact = new PeerContact(
            _ownerId,
            _peerId,
            ContactNickname.Create("Bob", _peerId),
            PeerTrustLevel.Verified,
            _fixedTime,
            initialKey);

        Assert.That(contact.TrustLevel, Is.EqualTo(PeerTrustLevel.Verified));

        // Act: Peer rotates their key (safety number changes)
        Span<byte> newKeyBytes = stackalloc byte[32];
        newKeyBytes.Fill(99);
        var newKey = IdentityKey.FromSpan(newKeyBytes);

        var result = contact.RotatePrimaryPublicKey(newKey);

        // Assert: Trust drops to Untrusted per Signal safety number change invariant
        Assert.That(result.IsSuccess, Is.True);
        Assert.That(contact.TrustLevel, Is.EqualTo(PeerTrustLevel.Untrusted));
        Assert.That(contact.PrimaryPublicKey, Is.EqualTo(newKey));
    }

    [Test]
    public void RotatePrimaryPublicKey_WithSameKey_IsIdempotentAndPreservesTrust()
    {
        // Arrange
        var initialKey = IdentityKey.FromSpan(new byte[32]);
        var contact = new PeerContact(
            _ownerId,
            _peerId,
            ContactNickname.Create("Bob", _peerId),
            PeerTrustLevel.Verified,
            _fixedTime,
            initialKey);

        // Act
        var result = contact.RotatePrimaryPublicKey(initialKey);

        // Assert
        Assert.That(result.IsSuccess, Is.True);
        Assert.That(contact.TrustLevel, Is.EqualTo(PeerTrustLevel.Verified));
    }

    [Test]
    public void RotateSignedPreKey_UpdatesTimestamp()
    {
        // Arrange
        var initialRotation = _fixedTime;
        var state = new PreKeyBundleState(_ownerId, DeviceId.Primary, 10, initialRotation);
        var newRotationTime = initialRotation.AddDays(7);

        // Act
        state.RotateSignedPreKey(newRotationTime);

        // Assert
        Assert.That(state.SignedPreKeyCreatedAtUtc, Is.EqualTo(newRotationTime));
    }
}

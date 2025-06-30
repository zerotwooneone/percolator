using FluentAssertions;
using Percolator.Application.Identity;
using Percolator.Identity;

namespace Percolator.ApplicationTests;

public class InMemoryPeerIdentityStoreTests
{
    private readonly InMemoryPeerIdentityStore _sut;

    public InMemoryPeerIdentityStoreTests()
    {
        _sut = new InMemoryPeerIdentityStore();
    }

    [Test]
    public async Task StorePeerAsync_ShouldStorePeerSuccessfully()
    {
        // Arrange
        var identityKey = new byte[] { 1, 2, 3 };
        var preKeyBundle = new byte[] { 4, 5, 6 };
        var peerIdentity = new PeerIdentity(identityKey, preKeyBundle);

        // Act
        await _sut.StorePeerAsync(peerIdentity);

        // Assert
        var retrievedPeer = await _sut.GetPeerAsync(identityKey);
        retrievedPeer.Should().NotBeNull();
        retrievedPeer!.IdentityKey.Should().Equal(identityKey);
        retrievedPeer.PreKeyBundle.Should().Equal(preKeyBundle);
    }

    [Test]
    public async Task GetPeerAsync_ShouldReturnNull_WhenPeerDoesNotExist()
    {
        // Arrange
        var identityKey = new byte[] { 9, 8, 7 };

        // Act
        var retrievedPeer = await _sut.GetPeerAsync(identityKey);

        // Assert
        retrievedPeer.Should().BeNull();
    }

    [Test]
    public async Task StorePeerAsync_ShouldUpdatePeer_WhenIdentityKeyAlreadyExists()
    {
        // Arrange
        var identityKey = new byte[] { 1, 2, 3 };
        var initialPreKeyBundle = new byte[] { 4, 5, 6 };
        var updatedPreKeyBundle = new byte[] { 7, 8, 9 };

        var initialPeerIdentity = new PeerIdentity(identityKey, initialPreKeyBundle);
        var updatedPeerIdentity = new PeerIdentity(identityKey, updatedPreKeyBundle);

        await _sut.StorePeerAsync(initialPeerIdentity);

        // Act
        await _sut.StorePeerAsync(updatedPeerIdentity);

        // Assert
        var retrievedPeer = await _sut.GetPeerAsync(identityKey);
        retrievedPeer.Should().NotBeNull();
        retrievedPeer!.IdentityKey.Should().Equal(identityKey);
        retrievedPeer.PreKeyBundle.Should().Equal(updatedPreKeyBundle); // Should be updated
    }
}

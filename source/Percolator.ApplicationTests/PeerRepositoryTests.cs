using FluentAssertions;
using Percolator.Application.Identity;
using IdentityPeerId = Percolator.Identity.PeerId;

namespace Percolator.ApplicationTests;

public class PeerRepositoryTests
{
    private readonly PeerRepository _sut;

    public PeerRepositoryTests()
    {
        _sut = new PeerRepository();
    }

    [Test]
    public async Task GetByIdAsync_ShouldReturnNull_WhenPeerDoesNotExist()
    {
        // Arrange
        var peerId = IdentityPeerId.NewId(); 

        // Act
        var peer = await _sut.GetByIdAsync(peerId);

        // Assert
        peer.Should().BeNull();
    }

    [Test]
    public async Task GetByThumbprintAsync_ShouldReturnNull_WhenPeerDoesNotExist()
    {
        // Arrange
        var thumbprint = "nonexistentthumbprint";

        // Act
        var peer = await _sut.GetByThumbprintAsync(thumbprint);

        // Assert
        peer.Should().BeNull();
    }

    // Note: PeerRepository currently has no 'Add' or 'Store' method exposed
    // to add peers for testing retrieval of existing peers. 
    // This would typically be handled by an external mechanism or a mock.
    // For now, only testing the 'not found' scenarios.
}

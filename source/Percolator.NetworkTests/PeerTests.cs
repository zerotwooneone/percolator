using FluentAssertions;
using Percolator.Network;
using System.Net;
using System.Security.Cryptography;

namespace Percolator.NetworkTests;

[TestFixture]
public class PeerTests
{
    [Test]
    public void Equals_WithSamePublicKeyHash_ShouldBeTrue()
    {
        // Arrange
        var hashBytes = SHA256.HashData("key1"u8.ToArray());
        var publicKeyHash = new PublicKeyHash(hashBytes);

        // Create two peers with the same public key hash but different endpoints and session IDs
        var peer1 = new Peer(PeerId.NewId(), IPAddress.Parse("127.0.0.1"), 1234, publicKeyHash);
        var peer2 = new Peer(PeerId.NewId(), IPAddress.Parse("192.168.1.1"), 5678, publicKeyHash);

        // Act & Assert: They should be considered the same peer because their identity is the same.
        peer1.Should().Be(peer2);
        (peer1 == peer2).Should().BeTrue();
        (peer1 != peer2).Should().BeFalse();
        peer1.GetHashCode().Should().Be(peer2.GetHashCode());
    }

    [Test]
    public void Equals_WithDifferentPublicKeyHash_ShouldBeFalse()
    {
        // Arrange
        var hashBytes1 = SHA256.HashData("key1"u8.ToArray());
        var publicKeyHash1 = new PublicKeyHash(hashBytes1);

        var hashBytes2 = SHA256.HashData("key2"u8.ToArray());
        var publicKeyHash2 = new PublicKeyHash(hashBytes2);

        // Create two peers with different public key hashes but the same endpoint
        var peer1 = new Peer(PeerId.NewId(), IPAddress.Parse("127.0.0.1"), 1234, publicKeyHash1);
        var peer2 = new Peer(PeerId.NewId(), IPAddress.Parse("127.0.0.1"), 1234, publicKeyHash2);

        // Act & Assert: They should be considered different peers.
        peer1.Should().NotBe(peer2);
        (peer1 != peer2).Should().BeTrue();
        (peer1 == peer2).Should().BeFalse();
    }

    [Test]
    public void Constructor_ShouldSetPropertiesCorrectly()
    {
        // Arrange
        var id = PeerId.NewId();
        var ipAddress = IPAddress.Parse("127.0.0.1");
        var port = 1234;
        var hashBytes = SHA256.HashData("key"u8.ToArray());
        var publicKeyHash = new PublicKeyHash(hashBytes);

        // Act
        var peer = new Peer(id, ipAddress, port, publicKeyHash);

        // Assert
        peer.Id.Should().Be(id);
        peer.GrpcEndpoint.Address.Should().Be(ipAddress);
        peer.GrpcEndpoint.Port.Should().Be(port);
        peer.PublicKeyHash.Should().Be(publicKeyHash);
        peer.LastSeenUtc.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(1));
    }
}

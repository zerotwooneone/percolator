using FluentAssertions;
using Percolator.Network;
using System.Net;

namespace Percolator.NetworkTests
{
    [TestFixture]
    public class PeerTests
    {
        [Test]
        public void Equals_WithSameId_ShouldBeTrue()
        {
            // Arrange
            var id = PeerId.NewId();
            var peer1 = new Peer(id, IPAddress.Parse("127.0.0.1"), 1234, "thumbprint1");
            var peer2 = new Peer(id, IPAddress.Parse("127.0.0.2"), 5678, "thumbprint2");

            // Act & Assert
            peer1.Should().Be(peer2);
            (peer1 == peer2).Should().BeTrue();
            (peer1 != peer2).Should().BeFalse();
        }

        [Test]
        public void Equals_WithDifferentId_ShouldBeFalse()
        {
            // Arrange
            var ipAddress = IPAddress.Parse("127.0.0.1");
            var port = 1234;
            var thumbprint = "thumbprint1";
            var peer1 = new Peer(PeerId.NewId(), ipAddress, port, thumbprint);
            var peer2 = new Peer(PeerId.NewId(), ipAddress, port, thumbprint);

            // Act & Assert
            peer1.Should().NotBe(peer2);
            (peer1 != peer2).Should().BeTrue();
            (peer1 == peer2).Should().BeFalse();
        }

        [Test]
        public void Equals_WithNull_ShouldBeFalse()
        {
            // Arrange
            var peer1 = new Peer(PeerId.NewId(), IPAddress.Parse("127.0.0.1"), 1234, "thumbprint1");

            // Act & Assert
            peer1.Equals(null).Should().BeFalse();
        }

        [Test]
        public void GetHashCode_WithSameId_ShouldBeEqual()
        {
            // Arrange
            var id = PeerId.NewId();
            var peer1 = new Peer(id, IPAddress.Parse("127.0.0.1"), 1234, "thumbprint1");
            var peer2 = new Peer(id, IPAddress.Parse("127.0.0.2"), 5678, "thumbprint2");

            // Act & Assert
            peer1.GetHashCode().Should().Be(peer2.GetHashCode());
        }

        [Test]
        public void GetHashCode_WithDifferentId_ShouldNotBeEqual()
        {
            // Arrange
            var ipAddress = IPAddress.Parse("127.0.0.1");
            var port = 1234;
            var thumbprint = "thumbprint1";
            var peer1 = new Peer(PeerId.NewId(), ipAddress, port, thumbprint);
            var peer2 = new Peer(PeerId.NewId(), ipAddress, port, thumbprint);

            // Act & Assert
            peer1.GetHashCode().Should().NotBe(peer2.GetHashCode());
        }
    }
}

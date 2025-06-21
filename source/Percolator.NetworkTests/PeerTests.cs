using FluentAssertions;
using Percolator.Network;
using System.Net;

namespace Percolator.NetworkTests
{
    [TestFixture]
    public class PeerTests
    {
        [Test]
        public void Equals_WithSameIpPortAndThumbprint_ShouldBeTrue()
        {
            // Arrange
            var ip = IPAddress.Parse("192.168.1.1");
            var peer1 = new Peer(ip, 8080, "thumbprint1");
            var peer2 = new Peer(ip, 8080, "thumbprint1");

            // Act & Assert
            peer1.Should().Be(peer2);
            (peer1 == peer2).Should().BeTrue();
            (peer1 != peer2).Should().BeFalse();
        }

        [Test]
        public void Equals_WithDifferentIp_ShouldBeFalse()
        {
            // Arrange
            var peer1 = new Peer(IPAddress.Parse("192.168.1.1"), 8080, "thumbprint1");
            var peer2 = new Peer(IPAddress.Parse("192.168.1.2"), 8080, "thumbprint1");

            // Act & Assert
            peer1.Should().NotBe(peer2);
            (peer1 != peer2).Should().BeTrue();
            (peer1 == peer2).Should().BeFalse();
        }

        [Test]
        public void Equals_WithDifferentPort_ShouldBeFalse()
        {
            // Arrange
            var ip = IPAddress.Parse("192.168.1.1");
            var peer1 = new Peer(ip, 8080, "thumbprint1");
            var peer2 = new Peer(ip, 8081, "thumbprint1");

            // Act & Assert
            peer1.Should().NotBe(peer2);
            (peer1 != peer2).Should().BeTrue();
            (peer1 == peer2).Should().BeFalse();
        }

        [Test]
        public void Equals_WithDifferentThumbprint_ShouldBeFalse()
        {
            // Arrange
            var ip = IPAddress.Parse("192.168.1.1");
            var peer1 = new Peer(ip, 8080, "thumbprint1");
            var peer2 = new Peer(ip, 8080, "thumbprint2");

            // Act & Assert
            peer1.Should().NotBe(peer2);
            (peer1 != peer2).Should().BeTrue();
            (peer1 == peer2).Should().BeFalse();
        }

        [Test]
        public void Equals_WithNull_ShouldBeFalse()
        {
            // Arrange
            var peer1 = new Peer(IPAddress.Parse("192.168.1.1"), 8080, "thumbprint1");

            // Act & Assert
            peer1.Equals(null).Should().BeFalse();
        }

        [Test]
        public void GetHashCode_WithSameIpAndPort_ShouldBeEqual()
        {
            // Arrange
            var ip = IPAddress.Parse("192.168.1.1");
            var peer1 = new Peer(ip, 8080, "thumbprint1");
            var peer2 = new Peer(ip, 8080, "thumbprint1");

            // Act & Assert
            peer1.GetHashCode().Should().Be(peer2.GetHashCode());
        }

        [Test]
        public void GetHashCode_WithDifferentThumbprint_ShouldNotBeEqual()
        {
            // Arrange
            var ip = IPAddress.Parse("192.168.1.1");
            var peer1 = new Peer(ip, 8080, "thumbprint1");
            var peer2 = new Peer(ip, 8080, "thumbprint2");

            // Act & Assert
            peer1.GetHashCode().Should().NotBe(peer2.GetHashCode());
        }
    }
}

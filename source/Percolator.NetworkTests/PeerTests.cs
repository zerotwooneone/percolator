using NUnit.Framework;
using Percolator.Network;
using System.Net;

namespace Percolator.NetworkTests
{
    [TestFixture]
    public class PeerTests
    {
        [Test]
        public void Equals_WithSameIpAndPort_ReturnsTrue()
        {
            var ipAddress = IPAddress.Parse("192.168.1.1");
            var peer1 = new Peer(ipAddress, 8080);
            var peer2 = new Peer(ipAddress, 8080);

            Assert.That(peer1.Equals(peer2), Is.True);
            Assert.That(peer2.Equals(peer1), Is.True);
            Assert.That(peer1 == peer2, Is.True);
            Assert.That(peer1 != peer2, Is.False);
        }

        [Test]
        public void Equals_WithDifferentIpAddress_ReturnsFalse()
        {
            var peer1 = new Peer(IPAddress.Parse("192.168.1.1"), 8080);
            var peer2 = new Peer(IPAddress.Parse("192.168.1.2"), 8080);

            Assert.That(peer1.Equals(peer2), Is.False);
            Assert.That(peer1 == peer2, Is.False);
            Assert.That(peer1 != peer2, Is.True);
        }

        [Test]
        public void Equals_WithDifferentPort_ReturnsFalse()
        {
            var ipAddress = IPAddress.Parse("192.168.1.1");
            var peer1 = new Peer(ipAddress, 8080);
            var peer2 = new Peer(ipAddress, 8081);

            Assert.That(peer1.Equals(peer2), Is.False);
            Assert.That(peer1 == peer2, Is.False);
            Assert.That(peer1 != peer2, Is.True);
        }

        [Test]
        public void Equals_WithNull_ReturnsFalse()
        {
            var peer1 = new Peer(IPAddress.Parse("192.168.1.1"), 8080);
            Assert.That(peer1.Equals(null), Is.False);
        }

        [Test]
        public void GetHashCode_ForEqualObjects_IsSame()
        {
            var ipAddress = IPAddress.Parse("192.168.1.1");
            var peer1 = new Peer(ipAddress, 8080);
            var peer2 = new Peer(ipAddress, 8080);

            Assert.That(peer1.GetHashCode(), Is.EqualTo(peer2.GetHashCode()));
        }
    }
}

using AutoFixture;
using FluentAssertions;
using Moq;
using NUnit.Framework;
using Percolator.Network;
using System.Linq;
using System.Threading.Tasks;

namespace Percolator.NetworkTests
{
    [TestFixture]
    public class PeerDiscoveryServiceTests
    {
        [Test]
        public void PeerExpiration_RemovesStalePeersAndNotifiesHandler()
        {
            // Arrange
            var fixture = new Fixture();
            var handlerMock = new Mock<IPeerDiscoveryHandler>();
            fixture.Register(() => handlerMock.Object);

            var service = fixture.Create<PeerDiscoveryService>();
            var peer = fixture.Create<Peer>();

            // Set the peer's state and add it to the service using the test helper
            peer.LastSeenUtc = System.DateTime.UtcNow.AddSeconds(-40);
            service.AddPeerForTesting(peer);

            service.DiscoveredPeers.Should().HaveCount(1, "because a peer was added for testing");

            // Act
            service.CleanupExpiredPeers();

            // Assert
            service.DiscoveredPeers.Should().BeEmpty("because the stale peer should have been removed");
            handlerMock.Verify(h => h.HandlePeerExpiredAsync(peer), Times.Once,
                "the handler should be notified exactly once when a peer expires");
        }
    }
}

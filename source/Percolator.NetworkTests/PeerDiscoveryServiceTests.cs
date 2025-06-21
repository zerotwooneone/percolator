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
        private Fixture _fixture;
        private Mock<IPeerDiscoveryHandler> _handlerMock;
        private PeerDiscoveryService _service;

        [SetUp]
        public void Setup()
        {
            _fixture = new Fixture();
            _handlerMock = new Mock<IPeerDiscoveryHandler>();
            _service = new PeerDiscoveryService(9000, "test_thumbprint", _handlerMock.Object);
        }

        [TearDown]
        public void TearDown()
        {
            _service.Dispose();
        }

        [Test]
        public void PeerExpiration_RemovesStalePeersAndNotifiesHandler()
        {
            // Arrange
            var peer = _fixture.Create<Peer>();

            // Set the peer's state and add it to the service using the test helper
            peer.LastSeenUtc = System.DateTime.UtcNow.AddSeconds(-40);
            _service.AddPeerForTesting(peer);

            _service.DiscoveredPeers.Should().HaveCount(1, "because a peer was added for testing");

            // Act
            _service.CleanupExpiredPeers();

            // Assert
            _service.DiscoveredPeers.Should().BeEmpty("because the stale peer should have been removed");
            _handlerMock.Verify(h => h.HandlePeerExpiredAsync(peer), Times.Once,
                "the handler should be notified exactly once when a peer expires");
        }
    }
}

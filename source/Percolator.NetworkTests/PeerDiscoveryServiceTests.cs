using NUnit.Framework;
using Percolator.Network;
using System.Linq;
using System.Net;
using System.Threading.Tasks;

namespace Percolator.NetworkTests
{
    [TestFixture]
    public class PeerDiscoveryServiceTests
    {
        [Test]
        public async Task PeerExpiration_RemovesStalePeers()
        {
            // Arrange
            var service = new PeerDiscoveryService(5000);
            var peer = new Peer(IPAddress.Loopback, 5001);

            // Manually add a peer to the internal dictionary for testing purposes
            var peersField = typeof(PeerDiscoveryService).GetField("_peers", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            Assert.That(peersField, Is.Not.Null, "_peers field not found.");
            var peersDictionary = peersField.GetValue(service) as System.Collections.Concurrent.ConcurrentDictionary<IPEndPoint, Peer>;
            Assert.That(peersDictionary, Is.Not.Null, "_peers dictionary is null.");

            // Set the LastSeenUtc to be older than the expiration time
            peer.LastSeenUtc = System.DateTime.UtcNow.AddSeconds(-40);
            peersDictionary[peer.GrpcEndpoint] = peer;

            Assert.That(service.DiscoveredPeers.Count(), Is.EqualTo(1), "Pre-condition: Peer should be in the list.");

            // Act
            var cleanupTaskMethod = typeof(PeerDiscoveryService).GetMethod("CleanupExpiredPeersAsync", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            Assert.That(cleanupTaskMethod, Is.Not.Null, "CleanupExpiredPeersAsync method not found.");
            var task = cleanupTaskMethod.Invoke(service, new object[] { new System.Threading.CancellationToken() }) as Task;
            Assert.That(task, Is.Not.Null, "Cleanup task is null.");

            // We don't await the task directly as it's an infinite loop.
            // Instead, we give it a moment to run one cleanup cycle.
            await Task.Delay(100);

            // Assert
            Assert.That(service.DiscoveredPeers.Count(), Is.EqualTo(0), "Post-condition: Peer should have been removed.");
        }
    }
}

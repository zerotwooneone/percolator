using Moq;
using NUnit.Framework;
using Percolator.Dht.Primitives;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Threading.Tasks;

namespace Percolator.Dht.Tests;

[TestFixture]
public class DhtServiceTests
{
    [Test]
    public async Task GetClosestNodesAsync_ShouldReturnNodesOrderedByXorDistance()
    {
        // Arrange
        var repositoryMock = new Mock<IDhtNodeRepository>();

        var targetId = new NodeId(SHA256.HashData(new byte[] { 1 }));

        // Create a list of nodes with varying distances to the target
        var nodes = new List<DhtNode>
        {
            // Closer nodes
            new(new NodeId(SHA256.HashData(new byte[] { 2 })), new DnsEndPoint("endpoint", 1), System.DateTimeOffset.UtcNow),
            new(new NodeId(SHA256.HashData(new byte[] { 3 })), new DnsEndPoint("endpoint", 2), System.DateTimeOffset.UtcNow),
            // Farther nodes
            new(new NodeId(SHA256.HashData(new byte[] { 255 })), new DnsEndPoint("endpoint", 3), System.DateTimeOffset.UtcNow),
            new(new NodeId(SHA256.HashData(new byte[] { 254 })), new DnsEndPoint("endpoint", 4), System.DateTimeOffset.UtcNow)
        };

        repositoryMock.Setup(r => r.GetAllAsync(CancellationToken.None)).ReturnsAsync(nodes);

        var service = new DhtService(repositoryMock.Object);

        // Act
        var result = await service.GetClosestNodesAsync(targetId);

        // Assert
        var expectedOrder = nodes
            .OrderBy(n => NodeId.XorDistance(n.Id, targetId))
            .ToList();

        Assert.That(result.ToList(), Is.EqualTo(expectedOrder));
    }
}

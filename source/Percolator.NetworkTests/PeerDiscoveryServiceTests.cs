using FluentAssertions;
using Moq;
using Percolator.Network;
using Microsoft.Extensions.Logging;

namespace Percolator.NetworkTests
{
    [TestFixture]
    public class PeerDiscoveryServiceTests
    {
        private Mock<IPeerDiscoveryHandler> _mockHandler;
        private Mock<IDiscoverySignatureProvider> _mockSignatureProvider;
        private Mock<ILogger<PeerDiscoveryService>> _mockLogger;
        private Mock<IPeerDiscoveryConfig> _mockConfig;
        private PeerDiscoveryService _service;

        [SetUp]
        public void Setup()
        {
            _mockHandler = new Mock<IPeerDiscoveryHandler>();
            _mockSignatureProvider = new Mock<IDiscoverySignatureProvider>();
            _mockLogger = new Mock<ILogger<PeerDiscoveryService>>();
            _mockConfig = new Mock<IPeerDiscoveryConfig>();

            _mockConfig.SetupGet(c => c.BroadcastPort).Returns(8888);
            _mockConfig.SetupGet(c => c.ListenPort).Returns(5001);
            _mockConfig.SetupGet(c => c.BroadcastInterval).Returns(TimeSpan.FromSeconds(5));
            _mockConfig.SetupGet(c => c.PeerExpiration).Returns(TimeSpan.FromSeconds(30));

            _service = new PeerDiscoveryService(
                _mockConfig.Object,
                "test_thumbprint",
                _mockHandler.Object,
                _mockSignatureProvider.Object,
                _mockLogger.Object);
        }

        [TearDown]
        public void TearDown()
        {
            _service.Dispose();
        }

        [Test]
        public void Service_CanBeConstructedAndDisposed()
        {
            // This test verifies that the service can be instantiated and disposed
            // without throwing exceptions, ensuring the DI setup is correct.
            _service.Should().NotBeNull();
        }
    }
}

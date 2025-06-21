using FluentAssertions;
using Moq;
using Percolator.Network;
using Microsoft.Extensions.Logging.Abstractions;

namespace Percolator.NetworkTests
{
    [TestFixture]
    public class PeerDiscoveryServiceTests
    {
        private Mock<IPeerDiscoveryHandler> _handlerMock;
        private Mock<IDiscoverySignatureProvider> _signatureProviderMock;
        private PeerDiscoveryService _service;

        [SetUp]
        public void Setup()
        {
            _handlerMock = new Mock<IPeerDiscoveryHandler>();
            _signatureProviderMock = new Mock<IDiscoverySignatureProvider>();

            _service = new PeerDiscoveryService(
                9000,
                "test_thumbprint",
                _handlerMock.Object,
                _signatureProviderMock.Object,
                NullLogger<PeerDiscoveryService>.Instance);
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

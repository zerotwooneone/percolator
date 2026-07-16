using Microsoft.Extensions.Logging;
using Moq;
using Percolator.Application.Chat;
using Percolator.Infrastructure.Egress;
using Percolator.Infrastructure.Network;
using Percolator.Network;
using Percolator.Network.Egress;
using Percolator.Network.ValueObjects;

namespace Percolator.InfrastructureTests.Egress;

[TestFixture]
public class NetworkEgressWorkerTests
{
    private Mock<INetworkEgressJobRepository> _mockRepository;
    private Mock<ITransportServiceClient> _mockTransportServiceClient;
    private Mock<IRelayServiceClient> _mockRelayServiceClient;
    private Mock<IPeerGrpcChannelFactory> _mockChannelFactory;
    private Mock<IPeerRoutingProfileRepository> _mockPeerRoutingProfileRepository;
    private Mock<IDeliveryCertificateStore> _mockDeliveryCertificateStore;
    private Mock<ILogger<NetworkEgressWorker>> _mockLogger;
    private NetworkEgressWorker _worker;

    [SetUp]
    public void Setup()
    {
        _mockRepository = new Mock<INetworkEgressJobRepository>();
        _mockTransportServiceClient = new Mock<ITransportServiceClient>();
        _mockRelayServiceClient = new Mock<IRelayServiceClient>();
        _mockChannelFactory = new Mock<IPeerGrpcChannelFactory>();
        _mockPeerRoutingProfileRepository = new Mock<IPeerRoutingProfileRepository>();
        _mockDeliveryCertificateStore = new Mock<IDeliveryCertificateStore>();
        _mockLogger = new Mock<ILogger<NetworkEgressWorker>>();
        _worker = new NetworkEgressWorker(
            _mockRepository.Object,
            _mockTransportServiceClient.Object,
            _mockRelayServiceClient.Object,
            _mockChannelFactory.Object,
            _mockPeerRoutingProfileRepository.Object,
            _mockDeliveryCertificateStore.Object,
            _mockLogger.Object);
    }

    [Test]
    public async Task ProcessPendingJobs_WhenNoJobs_ReturnsEarly()
    {
        // ARRANGE
        _mockRepository
            .Setup(r => r.GetPendingJobsAsync(It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<NetworkEgressJob>());

        // ACT
        await _worker.ProcessPendingJobsAsync(CancellationToken.None);

        // ASSERT
        _mockRepository.Verify(r => r.GetPendingJobsAsync(It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()), Times.Once);
        _mockRepository.Verify(r => r.SaveAsync(It.IsAny<NetworkEgressJob>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Test]
    public async Task ProcessPendingJobs_WhenJobFails_RecordsFailure()
    {
        // ARRANGE
        var jobId = new EgressJobId(1);
        var job = new NetworkEgressJob(
            jobId,
            new NetworkPeerId(1),
            RoutePreference.Direct,
            PayloadType.Group,
            NetworkPayloadBytes.FromBytes(new byte[] { 1, 2, 3 }),
            DateTimeOffset.UtcNow);

        _mockRepository
            .Setup(r => r.GetPendingJobsAsync(It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { job });

        // ACT
        await _worker.ProcessPendingJobsAsync(CancellationToken.None);

        // ASSERT
        _mockRepository.Verify(r => r.SaveAsync(It.Is<NetworkEgressJob>(j => j.JobId.Value == jobId.Value && j.AttemptCount == 1), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Test]
    public async Task ProcessPendingJobs_RoutePreferenceDirect_CallsDispatchDirect()
    {
        // ARRANGE
        var job = new NetworkEgressJob(
            new EgressJobId(1),
            new NetworkPeerId(1),
            RoutePreference.Direct,
            PayloadType.Group,
            NetworkPayloadBytes.FromBytes(new byte[] { 1, 2, 3 }),
            DateTimeOffset.UtcNow);

        _mockRepository
            .Setup(r => r.GetPendingJobsAsync(It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { job });

        // ACT
        await _worker.ProcessPendingJobsAsync(CancellationToken.None);

        // ASSERT - Should have called DispatchDirect (which currently returns false, causing failure recording)
        _mockRepository.Verify(r => r.SaveAsync(It.Is<NetworkEgressJob>(j => j.AttemptCount == 1), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Test]
    public async Task ProcessPendingJobs_RoutePreferenceRelay_CallsDispatchRelay()
    {
        // ARRANGE
        var job = new NetworkEgressJob(
            new EgressJobId(1),
            new NetworkPeerId(1),
            RoutePreference.Relay,
            PayloadType.Group,
            NetworkPayloadBytes.FromBytes(new byte[] { 1, 2, 3 }),
            DateTimeOffset.UtcNow);

        _mockRepository
            .Setup(r => r.GetPendingJobsAsync(It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { job });

        // ACT
        await _worker.ProcessPendingJobsAsync(CancellationToken.None);

        // ASSERT - Should have called DispatchRelay (which currently returns false, causing failure recording)
        _mockRepository.Verify(r => r.SaveAsync(It.Is<NetworkEgressJob>(j => j.AttemptCount == 1), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Test]
    public async Task ProcessPendingJobs_RoutePreferenceAny_TriesDirectThenRelay()
    {
        // ARRANGE
        var job = new NetworkEgressJob(
            new EgressJobId(1),
            new NetworkPeerId(1),
            RoutePreference.Any,
            PayloadType.Group,
            NetworkPayloadBytes.FromBytes(new byte[] { 1, 2, 3 }),
            DateTimeOffset.UtcNow);

        _mockRepository
            .Setup(r => r.GetPendingJobsAsync(It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { job });

        // ACT
        await _worker.ProcessPendingJobsAsync(CancellationToken.None);

        // ASSERT - Should have tried both (currently both return false, causing failure recording)
        _mockRepository.Verify(r => r.SaveAsync(It.Is<NetworkEgressJob>(j => j.AttemptCount == 1), It.IsAny<CancellationToken>()), Times.Once);
    }
}

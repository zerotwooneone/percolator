using FluentAssertions;
using NUnit.Framework;
using Percolator.Apps.Discovery.Features.NearbyPeerDiscovery;
using Percolator.Apps.Discovery.Ports;
using Percolator.Domain.Channels.ValueObjects;
using Percolator.Domain.Common;
using Percolator.Domain.Delivery.ValueObjects;
using Percolator.Domain.Identities.ValueObjects;
using Percolator.PluginSdk;

namespace Percolator.Apps.Discovery.Tests.NearbyPeerDiscovery;

[TestFixture]
public sealed class NearbyPeerDiscoveryWorkflowTests
{
    private FakeQueryClient _queryClient = null!;
    private FakePeerConnectionService _connectionService = null!;
    private NearbyPeerDiscoveryWorkflow _workflow = null!;

    private readonly PublicIdentityId _ownerId = PublicIdentityId.New();
    private readonly DeviceId _ownerDeviceId = new(1);
    private readonly PublicIdentityId _targetRelayId = PublicIdentityId.New();

    [SetUp]
    public void SetUp()
    {
        _queryClient = new FakeQueryClient();
        _connectionService = new FakePeerConnectionService();

        _workflow = new NearbyPeerDiscoveryWorkflow(
            _queryClient,
            _connectionService);
    }

    [Test]
    public async Task QueryNearbyPeersAsync_DoesNotConsumePreKeysOrInitiateConnections()
    {
        var peerA = PublicIdentityId.New();
        var peerB = PublicIdentityId.New();

        _queryClient.Candidates.Add(new PeerDiscoveryDescriptor(peerA, DeliveryRoute.RelayedOneToOne));
        _queryClient.Candidates.Add(new PeerDiscoveryDescriptor(peerB, DeliveryRoute.RelayedOneToOne));

        var results = await _workflow.QueryNearbyPeersAsync(_targetRelayId, DiscoveryResultLimit.Default);

        results.Should().HaveCount(2);
        // OPK Starvation check: Querying must NEVER initiate a connection or consume prekeys
        _connectionService.ConnectCallCount.Should().Be(0);
    }

    [Test]
    public async Task QueryNearbyPeersAsync_ClampsToRequestedLimit()
    {
        for (int i = 0; i < 10; i++)
        {
            _queryClient.Candidates.Add(new PeerDiscoveryDescriptor(PublicIdentityId.New(), DeliveryRoute.RelayedOneToOne));
        }

        var limit = new DiscoveryResultLimit(3);
        var results = await _workflow.QueryNearbyPeersAsync(_targetRelayId, limit);

        results.Should().HaveCount(3);
        _queryClient.LastRequestedLimit.Should().Be(limit);
    }

    [Test]
    public async Task DiscoverAndConnectAsync_SequentiallyConnectsToCandidates()
    {
        var peerA = PublicIdentityId.New();
        var peerB = PublicIdentityId.New();

        _queryClient.Candidates.Add(new PeerDiscoveryDescriptor(peerA, DeliveryRoute.RelayedOneToOne));
        _queryClient.Candidates.Add(new PeerDiscoveryDescriptor(peerB, DeliveryRoute.RelayedOneToOne));

        var result = await _workflow.DiscoverAndConnectAsync(
            _ownerId,
            _ownerDeviceId,
            _targetRelayId,
            DiscoveryResultLimit.Default);

        result.ConnectedPeers.Should().HaveCount(2);
        result.Failures.Should().BeEmpty();
        _connectionService.ConnectCallCount.Should().Be(2);
    }

    [Test]
    public async Task DiscoverAndConnectAsync_WhenOneCandidateFails_ContinuesWithRemainingCandidates()
    {
        var peerSuccess = PublicIdentityId.New();
        var peerFailing = PublicIdentityId.New();

        _queryClient.Candidates.Add(new PeerDiscoveryDescriptor(peerFailing, DeliveryRoute.RelayedOneToOne));
        _queryClient.Candidates.Add(new PeerDiscoveryDescriptor(peerSuccess, DeliveryRoute.RelayedOneToOne));

        // Configure peerFailing to fail connection
        _connectionService.FailForPeer(peerFailing, "PREKEY_NOT_FOUND");

        var result = await _workflow.DiscoverAndConnectAsync(
            _ownerId,
            _ownerDeviceId,
            _targetRelayId,
            DiscoveryResultLimit.Default);

        result.ConnectedPeers.Should().ContainSingle(p => p.PeerId == peerSuccess);
        result.Failures.Should().ContainSingle(f => f.PeerId == peerFailing && f.Error.Code == "PREKEY_NOT_FOUND");
        _connectionService.ConnectCallCount.Should().Be(2);
    }

    [Test]
    public void DiscoveryResultLimit_Create_RejectsValuesOutOfRange()
    {
        DiscoveryResultLimit.Create(0).IsSuccess.Should().BeFalse();
        DiscoveryResultLimit.Create(101).IsSuccess.Should().BeFalse();
        DiscoveryResultLimit.Create(50).IsSuccess.Should().BeTrue();
    }

    [Test]
    public void DiscoveryResultLimit_Clamp_RestrictsToMinAndMax()
    {
        DiscoveryResultLimit.Clamp(-5).Value.Should().Be(DiscoveryResultLimit.Min);
        DiscoveryResultLimit.Clamp(999).Value.Should().Be(DiscoveryResultLimit.Max);
        DiscoveryResultLimit.Clamp(42).Value.Should().Be(42);
    }

    // --- In-Memory Test Doubles ---

    private sealed class FakeQueryClient : INearbyDiscoveryQueryClientPort
    {
        public List<PeerDiscoveryDescriptor> Candidates { get; } = new();
        public DiscoveryResultLimit LastRequestedLimit { get; private set; }

        public Task<IReadOnlyList<PeerDiscoveryDescriptor>> SendNearbyQueryAsync(
            PublicIdentityId targetPeerOrRelayId,
            DiscoveryResultLimit limit,
            CancellationToken ct = default)
        {
            LastRequestedLimit = limit;
            return Task.FromResult<IReadOnlyList<PeerDiscoveryDescriptor>>(Candidates);
        }
    }

    private sealed class FakePeerConnectionService : IPeerConnectionService
    {
        public int ConnectCallCount { get; private set; }
        private readonly HashSet<PublicIdentityId> _failingPeers = new();
        private string _failureCode = "ERROR";

        public void FailForPeer(PublicIdentityId peerId, string errorCode)
        {
            _failingPeers.Add(peerId);
            _failureCode = errorCode;
        }

        public Task<DomainResult<PeerConnectionResult>> ConnectToPeerAsync(
            PublicIdentityId targetPeerId,
            DeviceId? targetDeviceId = null,
            RelayEndpoint? relayEndpoint = null,
            CancellationToken ct = default)
        {
            ConnectCallCount++;

            if (_failingPeers.Contains(targetPeerId))
            {
                return Task.FromResult(DomainResult<PeerConnectionResult>.Failure(
                    new DomainError(_failureCode, "Simulated connection error.")));
            }

            var channelId = ChannelId.New();
            var deviceId = targetDeviceId ?? new DeviceId(1);
            return Task.FromResult(DomainResult<PeerConnectionResult>.Success(
                new PeerConnectionResult(channelId, deviceId)));
        }
    }
}

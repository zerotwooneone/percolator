using FluentAssertions;
using NUnit.Framework;
using Percolator.Apps.Discovery.Features.NearbyPeerDiscovery;
using Percolator.Apps.Discovery.Ports;
using Percolator.Domain.Common;
using Percolator.Domain.Identities.ValueObjects;
using Percolator.Domain.Security.Model;
using Percolator.Domain.Security.ValueObjects;
using Percolator.PluginSdk;

namespace Percolator.Apps.Discovery.Tests.NearbyPeerDiscovery;

[TestFixture]
public sealed class NearbyPeerDiscoveryWorkflowTests
{
    private FakeQueryClient _queryClient = null!;
    private FakePreKeyProvider _preKeyProvider = null!;
    private FakeHandshakePort _handshakePort = null!;
    private NearbyPeerDiscoveryWorkflow _workflow = null!;

    private readonly PublicIdentityId _ownerId = PublicIdentityId.New();
    private readonly DeviceId _ownerDeviceId = new(1);
    private readonly PublicIdentityId _targetRelayId = PublicIdentityId.New();

    [SetUp]
    public void SetUp()
    {
        _queryClient = new FakeQueryClient();
        _preKeyProvider = new FakePreKeyProvider();
        _handshakePort = new FakeHandshakePort();

        _workflow = new NearbyPeerDiscoveryWorkflow(
            _queryClient,
            _preKeyProvider,
            _handshakePort);
    }

    [TearDown]
    public void TearDown()
    {
        _handshakePort.Dispose();
    }

    [Test]
    public async Task QueryNearbyPeersAsync_DoesNotConsumePreKeys()
    {
        var peerA = PublicIdentityId.New();
        var peerB = PublicIdentityId.New();

        _queryClient.Candidates.Add(new PeerDiscoveryDescriptor(peerA, DeliveryRoute.RelayedOneToOne));
        _queryClient.Candidates.Add(new PeerDiscoveryDescriptor(peerB, DeliveryRoute.RelayedOneToOne));

        var results = await _workflow.QueryNearbyPeersAsync(_targetRelayId, DiscoveryResultLimit.Default);

        results.Should().HaveCount(2);
        // OPK Starvation check: Querying must NEVER call the prekey bundle provider
        _preKeyProvider.FetchCallCount.Should().Be(0);
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
        _preKeyProvider.FetchCallCount.Should().Be(2);
        _handshakePort.HandshakeCallCount.Should().Be(2);
    }

    [Test]
    public async Task DiscoverAndConnectAsync_WhenOneCandidateFails_ContinuesWithRemainingCandidates()
    {
        var peerSuccess = PublicIdentityId.New();
        var peerFailing = PublicIdentityId.New();

        _queryClient.Candidates.Add(new PeerDiscoveryDescriptor(peerFailing, DeliveryRoute.RelayedOneToOne));
        _queryClient.Candidates.Add(new PeerDiscoveryDescriptor(peerSuccess, DeliveryRoute.RelayedOneToOne));

        // Configure peerFailing to fail prekey fetch
        _preKeyProvider.FailForPeer(peerFailing, "PREKEY_NOT_FOUND");

        var result = await _workflow.DiscoverAndConnectAsync(
            _ownerId,
            _ownerDeviceId,
            _targetRelayId,
            DiscoveryResultLimit.Default);

        result.ConnectedPeers.Should().ContainSingle(p => p.PeerId == peerSuccess);
        result.Failures.Should().ContainSingle(f => f.PeerId == peerFailing && f.Error.Code == "PREKEY_NOT_FOUND");
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

        public Task<IReadOnlyList<PeerDiscoveryDescriptor>> SendNearbyQueryAsync(
            PublicIdentityId targetPeerOrRelayId,
            int requestedLimit,
            CancellationToken ct = default)
        {
            return Task.FromResult<IReadOnlyList<PeerDiscoveryDescriptor>>(Candidates);
        }
    }

    private sealed class FakePreKeyProvider : IPreKeyBundleProvider
    {
        public int FetchCallCount { get; private set; }
        private readonly HashSet<PublicIdentityId> _failingPeers = new();
        private string _failureCode = "ERROR";

        public void FailForPeer(PublicIdentityId peerId, string errorCode)
        {
            _failingPeers.Add(peerId);
            _failureCode = errorCode;
        }

        public ValueTask<DomainResult<PreKeyBundle>> FetchConsumableBundleAsync(
            PublicIdentityId targetPeerId,
            CancellationToken ct = default)
        {
            FetchCallCount++;

            if (_failingPeers.Contains(targetPeerId))
            {
                return ValueTask.FromResult(DomainResult<PreKeyBundle>.Failure(new DomainError(_failureCode, "Simulated prekey error.")));
            }

            var identityKey = IdentityKey.FromSpan(new byte[32]);
            var signedPreKey = DhPublicKey.FromSpan(new byte[32]);
            var signature = DeviceLinkProof.FromSpan(new byte[64]);
            var bundle = new PreKeyBundle(
                targetPeerId,
                new DeviceId(1),
                identityKey,
                signedPreKey,
                signature);

            return ValueTask.FromResult(DomainResult<PreKeyBundle>.Success(bundle));
        }
    }

    private sealed class FakeHandshakePort : IDiscoveryHandshakePort, IDisposable
    {
        public int HandshakeCallCount { get; private set; }
        private readonly List<ChainKey> _createdKeys = new();

        public ValueTask<DomainResult<DirectRatchetSession>> InitiateHandshakeAsync(
            PublicIdentityId ownerId,
            DeviceId ownerDeviceId,
            PreKeyBundle remoteBundle,
            CancellationToken ct = default)
        {
            HandshakeCallCount++;

            var rootKey = ChainKey.FromSpan(new byte[32]);
            var sendingChain = ChainKey.FromSpan(new byte[32]);
            _createdKeys.Add(rootKey);
            _createdKeys.Add(sendingChain);

            var session = new DirectRatchetSession(
                ownerId,
                ownerDeviceId,
                remoteBundle.IdentityId,
                remoteBundle.DeviceId,
                rootKey: rootKey,
                sendingChainKey: sendingChain,
                receivingChainKey: null);

            return ValueTask.FromResult(DomainResult<DirectRatchetSession>.Success(session));
        }

        public void Dispose()
        {
            foreach (var key in _createdKeys)
            {
                key.Dispose();
            }
        }
    }
}

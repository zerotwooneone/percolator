using Percolator.Apps.FileTransfer.Domain.Common;
using Percolator.Apps.FileTransfer.Domain.ManifestManagement;
using Percolator.Apps.FileTransfer.Domain.SwarmCoordination;
using Percolator.Apps.FileTransfer.Domain.TransferNegotiation;
using Percolator.Apps.FileTransfer.Features.MultiStreamEngine;
using Percolator.Apps.FileTransfer.Features.SwarmCoordination;
using Percolator.Apps.FileTransfer.Features.TransferNegotiation;
using Percolator.Apps.FileTransfer.Tests.TestDoubles;
using Percolator.Domain.Common;
using Percolator.Domain.Identities.ValueObjects;

namespace Percolator.Apps.FileTransfer.Tests.Features.SwarmCoordination.Tests;

[TestFixture]
public sealed class SwarmCoordinationTests
{
    private FakeDateTimeProvider _clock = null!;
    private SwarmCoordinator _swarmCoordinator = null!;
    private EndgameCoordinator _endgameCoordinator = null!;

    [SetUp]
    public void SetUp()
    {
        _clock = new FakeDateTimeProvider();
        _swarmCoordinator = new SwarmCoordinator(_clock);
        _endgameCoordinator = new EndgameCoordinator();
    }

    [Test]
    public void UT_SWM_01_RarestFirstChunkSchedulingAcrossSwarm()
    {
        ChunkCount total = ChunkCount.FromInt(3);
        Bitfield local = Bitfield.Empty(total);
        Bitfield required = Bitfield.Full(total);

        byte[] p1Bytes = new byte[16]; p1Bytes[0] = 1;
        byte[] p2Bytes = new byte[16]; p2Bytes[0] = 2;
        byte[] p3Bytes = new byte[16]; p3Bytes[0] = 3;

        PublicIdentityId p1 = PublicIdentityId.FromBytes(p1Bytes);
        PublicIdentityId p2 = PublicIdentityId.FromBytes(p2Bytes);
        PublicIdentityId p3 = PublicIdentityId.FromBytes(p3Bytes);

        // Chunk 0: available on 3 peers (p1, p2, p3)
        // Chunk 1: available on 1 peer (p1)
        // Chunk 2: available on 2 peers (p1, p2)
        Bitfield b1 = Bitfield.Empty(total)
            .WithChunk(ChunkIndex.FromInt(0))
            .WithChunk(ChunkIndex.FromInt(1))
            .WithChunk(ChunkIndex.FromInt(2));

        Bitfield b2 = Bitfield.Empty(total)
            .WithChunk(ChunkIndex.FromInt(0))
            .WithChunk(ChunkIndex.FromInt(2));

        Bitfield b3 = Bitfield.Empty(total)
            .WithChunk(ChunkIndex.FromInt(0));

        SwarmMember m1 = new(p1, b1);
        m1.SetPeerChokingUs(PeerChokeStatus.Unchoked);

        SwarmMember m2 = new(p2, b2);
        m2.SetPeerChokingUs(PeerChokeStatus.Unchoked);

        SwarmMember m3 = new(p3, b3);
        m3.SetPeerChokingUs(PeerChokeStatus.Unchoked);

        ChunkIndex? selected = RarestFirstChunkSelector.SelectNextChunk(
            local,
            required,
            [m1, m2, m3],
            new HashSet<ChunkIndex>());

        selected.Should().NotBeNull();
        selected!.Value.Value.Should().Be(1); // Chunk 1 is rarest (rarity = 1)
    }

    [Test]
    public void UT_SWM_02_PexFrameIngestionRateLimitingViaVirtualClock()
    {
        PublicIdentityId peerId = PublicIdentityId.New();
        List<DirectEndpointCandidate> candidates =
        [
            new DirectEndpointCandidate(EndpointHost.Parse("192.168.1.10"), PortNumber.FromInt(5000), DirectCandidateKind.DirectLan)
        ];

        // First PEX frame is accepted
        bool firstAccepted = _swarmCoordinator.ProcessIncomingPex(
            peerId,
            candidates,
            DirectWanPolicy.DisabledByDefault,
            localEndpoints: null,
            out _);

        firstAccepted.Should().BeTrue();

        // Advance virtual clock by 4 seconds (< 10s rate limit)
        _clock.Advance(TimeSpan.FromSeconds(4));

        // Second PEX frame is dropped without processing
        bool secondAccepted = _swarmCoordinator.ProcessIncomingPex(
            peerId,
            candidates,
            DirectWanPolicy.DisabledByDefault,
            localEndpoints: null,
            out _);

        secondAccepted.Should().BeFalse();
    }

    [Test]
    public void UT_SWM_03_PexWanPolicyGateEnforcement()
    {
        PublicIdentityId peerId = PublicIdentityId.New();
        List<DirectEndpointCandidate> candidates =
        [
            new DirectEndpointCandidate(EndpointHost.Parse("192.168.1.10"), PortNumber.FromInt(5000), DirectCandidateKind.DirectLan),
            new DirectEndpointCandidate(EndpointHost.Parse("192.168.1.11"), PortNumber.FromInt(5001), DirectCandidateKind.DirectLan),
            new DirectEndpointCandidate(EndpointHost.Parse("203.0.113.1"), PortNumber.FromInt(6000), DirectCandidateKind.DirectWanUpnp),
            new DirectEndpointCandidate(EndpointHost.Parse("198.51.100.1"), PortNumber.FromInt(7000), DirectCandidateKind.DirectWanStun)
        ];

        bool accepted = _swarmCoordinator.ProcessIncomingPex(
            peerId,
            candidates,
            DirectWanPolicy.DisabledByDefault,
            localEndpoints: null,
            out IReadOnlyList<DirectEndpointCandidate> filtered);

        accepted.Should().BeTrue();
        filtered.Should().HaveCount(2);
        filtered.Should().OnlyContain(c => c.Kind == DirectCandidateKind.DirectLan);
    }

    [Test]
    public async Task UT_SWM_04_SwarmPeerHeartbeatAndIdleKeepAliveViaVirtualClock()
    {
        PublicIdentityId peerId = PublicIdentityId.New();
        _swarmCoordinator.RegisterPeer(peerId);

        FakeTransferStreamPool pool = new();
        pool.Streams.Add(new FakeTransferStream { Index = StreamIndex.FromInt(0) });

        // Advance virtual clock by 30 seconds
        _clock.Advance(TimeSpan.FromSeconds(30));

        await _swarmCoordinator.ProcessHeartbeatsAsync(pool);

        pool.BroadcastFrames.Should().ContainSingle(f => f.FrameType == StreamFrameType.KeepAlive);
    }

    [Test]
    public void UT_SWM_05_DeadPeerDetectionAndEndpointArchivalViaVirtualClock()
    {
        PublicIdentityId peerId = PublicIdentityId.New();
        _swarmCoordinator.RegisterPeer(peerId);

        // Advance virtual clock by 60 seconds
        _clock.Advance(TimeSpan.FromSeconds(60));

        IReadOnlyList<PublicIdentityId> deadPeers = _swarmCoordinator.DetectAndPruneDeadPeers();

        deadPeers.Should().ContainSingle(p => p == peerId);
    }

    [Test]
    public async Task UT_SWM_06_TargetSeedRatioAutoCompletion()
    {
        TransferSessionId sessionId = TransferSessionId.New();
        PublicIdentityId initiatorId = PublicIdentityId.New();
        PublicIdentityId peerId = PublicIdentityId.New();

        TransferManifest manifest = TransferManifest.Create(
            sessionId,
            RootBundleName.Parse("SeedRatioTest"),
            ChunkSizeBytes.Default,
            MerkleTreeBuilder.BuildFromLeaves([ChunkHash.FromBytes(new byte[32])]),
            [new FileEntry(FileIndex.FromInt(0), SanitizedRelativePath.Parse("data.bin"), FileSizeBytes.FromBytes(1000), BundleByteOffset.Zero, FileLastModifiedUtc.FromDateTimeOffset(DateTimeOffset.UtcNow))]);

        SeedRatio targetRatio = SeedRatio.FromMultiplier(1.5);
        TransferSession session = TransferSession.Rehydrate(
            sessionId,
            TransferRole.Sender,
            TransferSessionState.Seeding,
            new TransferTopology.OneToOne(initiatorId, peerId),
            manifest,
            Bitfield.Full(manifest.TotalChunks),
            SelectedFileIndices.All(manifest.TotalFiles),
            targetRatio,
            TotalBundleBytes.FromBytes(0),
            [new SwarmMember(peerId, Bitfield.Empty(manifest.TotalChunks))],
            _clock.UtcNow,
            null);

        RecordingTrackerPort tracker = new();
        FakeTransferStreamPool pool = new();

        // Upload 1500 bytes (1.5x of 1000 bytes)
        session.RecordUploadedBytes(TransferByteLength.FromBytes(1500), _clock);

        bool completed = await _swarmCoordinator.CheckSeedRatioCompletionAsync(session, tracker, pool);

        completed.Should().BeTrue();
        session.State.Should().Be(TransferSessionState.Completed);
        pool.IsDisposed.Should().BeTrue();
        tracker.Unregistered.Should().ContainSingle();
    }

    [Test]
    public async Task UT_SWM_07_EndgameModeChunkBroadcastAndCancellation()
    {
        ChunkCount total = ChunkCount.FromInt(4);
        // Only 2 chunks remain uncompleted (chunks 2 and 3)
        Bitfield local = Bitfield.Empty(total)
            .WithChunk(ChunkIndex.FromInt(0))
            .WithChunk(ChunkIndex.FromInt(1));

        Bitfield required = Bitfield.Full(total);

        _endgameCoordinator.IsInEndgame(local, required).Should().BeTrue();

        IReadOnlyList<ChunkIndex> remaining = _endgameCoordinator.GetRemainingChunks(local, required);
        remaining.Should().HaveCount(2);

        FakeTransferStreamPool pool = new();

        // Broadcast requests for remaining chunks
        await _endgameCoordinator.BroadcastEndgameRequestsAsync(pool, remaining);

        pool.BroadcastFrames.Should().HaveCount(2);
        pool.BroadcastFrames.Select(f => f.FrameType).Should().AllBeEquivalentTo(StreamFrameType.RequestChunk);

        // Once chunk 2 is received, broadcast cancel
        await _endgameCoordinator.BroadcastCancelChunkAsync(pool, ChunkIndex.FromInt(2));

        pool.BroadcastFrames.Last().FrameType.Should().Be(StreamFrameType.CancelChunk);
        ((CancelChunkFrame)pool.BroadcastFrames.Last()).ChunkIndex.Value.Should().Be(2);
    }

    [Test]
    public void UT_SWM_08_PexCandidateDeduplicationAndSelfEndpointFiltering()
    {
        DirectEndpointCandidate duplicate = new(EndpointHost.Parse("192.168.1.10"), PortNumber.FromInt(5000), DirectCandidateKind.DirectLan);
        DirectEndpointCandidate localSelf = new(EndpointHost.Parse("127.0.0.1"), PortNumber.FromInt(8080), DirectCandidateKind.DirectLan);
        DirectEndpointCandidate unique = new(EndpointHost.Parse("192.168.1.20"), PortNumber.FromInt(5000), DirectCandidateKind.DirectLan);

        List<DirectEndpointCandidate> raw = [duplicate, duplicate, localSelf, unique];
        List<DirectEndpointCandidate> localEndpoints = [localSelf];

        IReadOnlyList<DirectEndpointCandidate> filtered = PexRateLimiter.FilterAndDeduplicate(
            raw,
            DirectWanPolicy.ExplicitlyAllowed,
            localEndpoints);

        filtered.Should().HaveCount(2);
        filtered.Should().Contain([duplicate, unique]);
        filtered.Should().NotContain(localSelf);
    }
}

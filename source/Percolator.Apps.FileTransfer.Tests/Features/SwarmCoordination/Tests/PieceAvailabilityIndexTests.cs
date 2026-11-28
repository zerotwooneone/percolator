using FluentAssertions;
using NUnit.Framework;
using Percolator.Apps.FileTransfer.Domain.Common;
using Percolator.Apps.FileTransfer.Domain.TransferNegotiation;
using Percolator.Apps.FileTransfer.Features.MultiStreamEngine;
using Percolator.Apps.FileTransfer.Features.SwarmCoordination;
using PublicIdentityId = Percolator.Domain.Identities.PublicIdentityId;

namespace Percolator.Apps.FileTransfer.Tests.Features.SwarmCoordination.Tests;

[TestFixture]
public sealed class PieceAvailabilityIndexTests
{
    private PublicIdentityId _peer1;
    private PublicIdentityId _peer2;
    private PublicIdentityId _peer3;

    [SetUp]
    public void SetUp()
    {
        byte[] b1 = new byte[16]; b1[0] = 1;
        byte[] b2 = new byte[16]; b2[0] = 2;
        byte[] b3 = new byte[16]; b3[0] = 3;
        _peer1 = PublicIdentityId.FromBytes(b1);
        _peer2 = PublicIdentityId.FromBytes(b2);
        _peer3 = PublicIdentityId.FromBytes(b3);
    }

    [Test]
    public void UT_PAI_01_PieceAvailabilityTracksBitfieldAndHaveAccurately()
    {
        ChunkCount total = ChunkCount.FromInt(8);
        PieceAvailabilityIndex index = new(total);

        // Peer 1 has chunks 0, 1, 2
        Bitfield bf1 = Bitfield.Empty(total)
            .WithChunk(ChunkIndex.FromInt(0))
            .WithChunk(ChunkIndex.FromInt(1))
            .WithChunk(ChunkIndex.FromInt(2));
        index.SetPeerBitfield(_peer1, bf1);

        // Peer 2 has chunks 1, 2
        Bitfield bf2 = Bitfield.Empty(total)
            .WithChunk(ChunkIndex.FromInt(1))
            .WithChunk(ChunkIndex.FromInt(2));
        index.SetPeerBitfield(_peer2, bf2);

        index.GetAvailability(ChunkIndex.FromInt(0)).Should().Be(1);
        index.GetAvailability(ChunkIndex.FromInt(1)).Should().Be(2);
        index.GetAvailability(ChunkIndex.FromInt(2)).Should().Be(2);
        index.GetAvailability(ChunkIndex.FromInt(3)).Should().Be(0);

        // Peer 3 sends Have for chunk 0
        index.RecordPeerHave(_peer3, ChunkIndex.FromInt(0));
        index.GetAvailability(ChunkIndex.FromInt(0)).Should().Be(2);

        // Peer 1 disconnects / is removed
        index.RemovePeer(_peer1);
        index.GetAvailability(ChunkIndex.FromInt(0)).Should().Be(1); // only peer 3 now
        index.GetAvailability(ChunkIndex.FromInt(1)).Should().Be(1); // only peer 2 now
        index.GetAvailability(ChunkIndex.FromInt(2)).Should().Be(1); // only peer 2 now
    }

    [Test]
    public void UT_PAI_02_SelectRarestForPeer_PicksLowestAvailabilityPiece()
    {
        ChunkCount total = ChunkCount.FromInt(4);
        PieceAvailabilityIndex index = new(total);

        // Peer 1 has chunks 0, 1, 2
        // Peer 2 has chunks 0, 2
        // Chunk 0 avail: 2, Chunk 1 avail: 1, Chunk 2 avail: 2, Chunk 3 avail: 0
        Bitfield bf1 = Bitfield.Empty(total)
            .WithChunk(ChunkIndex.FromInt(0))
            .WithChunk(ChunkIndex.FromInt(1))
            .WithChunk(ChunkIndex.FromInt(2));
        Bitfield bf2 = Bitfield.Empty(total)
            .WithChunk(ChunkIndex.FromInt(0))
            .WithChunk(ChunkIndex.FromInt(2));

        index.SetPeerBitfield(_peer1, bf1);
        index.SetPeerBitfield(_peer2, bf2);

        Bitfield local = Bitfield.Empty(total);
        Bitfield required = Bitfield.Full(total);

        // For Peer 1, rarest chunk is chunk 1 (availability 1)
        ChunkIndex? selected = index.SelectRarestForPeer(_peer1, local, required, _ => false);
        selected.Should().Be(ChunkIndex.FromInt(1));

        // If chunk 1 is in-flight, it should pick chunk 0 or 2 (availability 2)
        selected = index.SelectRarestForPeer(_peer1, local, required, c => c.Value == 1);
        selected.Should().Be(ChunkIndex.FromInt(0));

        // If chunk 0 is already in local, it should pick chunk 2
        Bitfield localWith0 = local.WithChunk(ChunkIndex.FromInt(0));
        selected = index.SelectRarestForPeer(_peer1, localWith0, required, c => c.Value == 1);
        selected.Should().Be(ChunkIndex.FromInt(2));
    }

    [Test]
    public void UT_PAI_03_IntegrationWithChunkPipelineSchedulerTryScheduleRarest()
    {
        ChunkCount total = ChunkCount.FromInt(4);
        PieceAvailabilityIndex index = new(total);
        ChunkPipelineScheduler scheduler = new();

        Bitfield bf1 = Bitfield.Empty(total)
            .WithChunk(ChunkIndex.FromInt(0))
            .WithChunk(ChunkIndex.FromInt(1));
        index.SetPeerBitfield(_peer1, bf1);

        Bitfield local = Bitfield.Empty(total);
        Bitfield required = Bitfield.Full(total);

        bool scheduled = scheduler.TryScheduleRarest(
            _peer1,
            isInFlight => index.SelectRarestForPeer(_peer1, local, required, isInFlight),
            out ChunkIndex scheduledChunk);

        scheduled.Should().BeTrue();
        scheduledChunk.Should().Be(ChunkIndex.FromInt(0));
        scheduler.IsChunkInFlight(ChunkIndex.FromInt(0)).Should().BeTrue();

        // Second schedule should pick chunk 1
        scheduled = scheduler.TryScheduleRarest(
            _peer1,
            isInFlight => index.SelectRarestForPeer(_peer1, local, required, isInFlight),
            out scheduledChunk);

        scheduled.Should().BeTrue();
        scheduledChunk.Should().Be(ChunkIndex.FromInt(1));

        // Third schedule should fail because peer1 has no more pieces we need
        scheduled = scheduler.TryScheduleRarest(
            _peer1,
            isInFlight => index.SelectRarestForPeer(_peer1, local, required, isInFlight),
            out _);

        scheduled.Should().BeFalse();
    }
}

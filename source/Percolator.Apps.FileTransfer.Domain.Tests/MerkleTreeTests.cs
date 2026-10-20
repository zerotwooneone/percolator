using System.Security.Cryptography;
using Percolator.Apps.FileTransfer.Domain.Common;
using Percolator.Apps.FileTransfer.Domain.ManifestManagement;
using Percolator.Apps.FileTransfer.Domain.TransferNegotiation;
using Percolator.Apps.FileTransfer.Domain.TransferNegotiation.Events;
using Percolator.Apps.FileTransfer.Domain.Tests.TestDoubles;
using Percolator.Domain.Common;
using Percolator.Domain.Identities.ValueObjects;

namespace Percolator.Apps.FileTransfer.Domain.Tests;

[TestFixture]
public sealed class MerkleTreeTests
{
    [Test]
    public void DT_MRK_01_OddNodePromotionRule_OddNodePromotedDirectlyWithoutDuplicateHashing()
    {
        // 3 leaves
        byte[] d0 = [0xAA];
        byte[] d1 = [0xBB];
        byte[] d2 = [0xCC];

        ChunkHash leaf0 = MerkleTreeBuilder.HashLeaf(d0);
        ChunkHash leaf1 = MerkleTreeBuilder.HashLeaf(d1);
        ChunkHash leaf2 = MerkleTreeBuilder.HashLeaf(d2);

        MerkleTreeIndex tree = MerkleTreeBuilder.BuildFromLeaves([leaf0, leaf1, leaf2]);

        // Expected calculation:
        // Level 0: [leaf0, leaf1, leaf2]
        // Level 1: [ HashInterior(leaf0, leaf1), leaf2 (promoted directly) ]
        // Level 2 (Root): HashInterior( HashInterior(leaf0, leaf1), leaf2 )
        byte[] expectedParent01 = MerkleTreeBuilder.HashInterior(leaf0.Span, leaf1.Span);
        byte[] expectedRoot = MerkleTreeBuilder.HashInterior(expectedParent01, leaf2.Span);

        tree.Root.Span.ToArray().Should().Equal(expectedRoot);
    }

    [Test]
    public void DT_MRK_02_DomainSeparationPrefixes_LeafUses0x00_InteriorUses0x01()
    {
        byte[] data = [0x01, 0x02, 0x03];

        // Leaf hash
        ChunkHash leaf = MerkleTreeBuilder.HashLeaf(data);

        byte[] manualLeafPayload = new byte[1 + data.Length];
        manualLeafPayload[0] = 0x00;
        data.CopyTo(manualLeafPayload, 1);
        byte[] manualLeafHash = SHA256.HashData(manualLeafPayload);

        leaf.Span.ToArray().Should().Equal(manualLeafHash);

        // Interior hash
        byte[] left = new byte[32]; left[0] = 0xAA;
        byte[] right = new byte[32]; right[0] = 0xBB;
        byte[] interior = MerkleTreeBuilder.HashInterior(left, right);

        byte[] manualInteriorPayload = new byte[1 + 32 + 32];
        manualInteriorPayload[0] = 0x01;
        left.CopyTo(manualInteriorPayload, 1);
        right.CopyTo(manualInteriorPayload, 1 + 32);
        byte[] manualInteriorHash = SHA256.HashData(manualInteriorPayload);

        interior.Should().Equal(manualInteriorHash);
    }

    [Test]
    public void DT_MRK_03_CorruptedChunk_LeafVerificationRejection()
    {
        TransferManifest manifest = DomainTestHelper.CreateTestManifest(fileCount: 1, bytesPerFile: 1024 * 1024);
        PublicIdentityId initiatorId = DomainTestHelper.CreateIdentity();
        FakeDateTimeProvider clock = new();

        TransferSession session = TransferSession.CreateIncomingStaged(
            TransferSessionId.New(),
            initiatorId,
            manifest,
            SeedRatio.Zero,
            clock);

        session.AcceptTransfer(SelectedFileIndices.All(manifest.TotalFiles), clock);

        // Corrupted chunk data: flip some bits
        byte[] corruptedData = new byte[1024 * 1024];
        corruptedData[0] = 0xEE;

        // Act
        DomainResult result = session.VerifyAndRecordChunk(
            ChunkIndex.FromInt(0),
            PlaintextChunkData.FromBytes(corruptedData),
            clock);

        // Assert
        result.IsSuccess.Should().BeFalse();
        result.Error.Code.Should().Be("Transfer.CryptographicVerificationFailed");
        session.LocalBitfield.HasChunk(ChunkIndex.FromInt(0)).Should().BeFalse();
        session.DomainEvents.Should().NotContain(e => e is ChunkVerifiedDomainEvent);
    }

    [Test]
    public void DT_MRK_04_IdempotentChunkVerificationProtection()
    {
        TransferManifest manifest = DomainTestHelper.CreateTestManifest(fileCount: 2, bytesPerFile: 1024 * 1024);
        PublicIdentityId initiatorId = DomainTestHelper.CreateIdentity();
        FakeDateTimeProvider clock = new();

        TransferSession session = TransferSession.CreateIncomingStaged(
            TransferSessionId.New(),
            initiatorId,
            manifest,
            SeedRatio.FromMultiplier(1.0),
            clock);

        session.AcceptTransfer(SelectedFileIndices.All(manifest.TotalFiles), clock);

        byte[] validChunkData = new byte[1024 * 1024];
        BitConverter.GetBytes(0).CopyTo(validChunkData, 0);

        // First verification
        DomainResult result1 = session.VerifyAndRecordChunk(
            ChunkIndex.FromInt(0),
            PlaintextChunkData.FromBytes(validChunkData),
            clock);
        result1.IsSuccess.Should().BeTrue();

        int eventsCount = session.DomainEvents.Count;

        // Second verification (idempotent)
        DomainResult result2 = session.VerifyAndRecordChunk(
            ChunkIndex.FromInt(0),
            PlaintextChunkData.FromBytes(validChunkData),
            clock);
        result2.IsSuccess.Should().BeTrue();

        // Emits zero duplicate events
        session.DomainEvents.Count.Should().Be(eventsCount);
    }

    [Test]
    public void DT_MRK_05_MerkleTree_LeafCountBoundaryTopologies()
    {
        int[] counts = [1, 2, 4, 5];
        List<MerkleTreeIndex> trees = [];

        foreach (int count in counts)
        {
            List<ChunkHash> leaves = [];
            for (int i = 0; i < count; i++)
            {
                leaves.Add(MerkleTreeBuilder.HashLeaf([(byte)i]));
            }

            MerkleTreeIndex tree = MerkleTreeBuilder.BuildFromLeaves(leaves);
            tree.LeafCount.Should().Be(count);
            tree.Root.Span.Length.Should().Be(32);
            trees.Add(tree);

            // Verify each leaf
            for (int i = 0; i < count; i++)
            {
                tree.VerifyLeaf(ChunkIndex.FromInt(i), [(byte)i]).Should().BeTrue();
                tree.VerifyLeaf(ChunkIndex.FromInt(i), [(byte)(i + 100)]).Should().BeFalse();
            }
        }

        // Distinct topologies produce distinct roots
        for (int i = 0; i < trees.Count; i++)
        {
            for (int j = i + 1; j < trees.Count; j++)
            {
                trees[i].Root.Span.SequenceEqual(trees[j].Root.Span).Should().BeFalse();
            }
        }
    }
}

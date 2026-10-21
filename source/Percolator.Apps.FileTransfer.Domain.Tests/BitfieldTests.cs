using Percolator.Apps.FileTransfer.Domain.Common;
using Percolator.Apps.FileTransfer.Domain.Exceptions;
using Percolator.Apps.FileTransfer.Domain.ManifestManagement;
using Percolator.Apps.FileTransfer.Domain.TransferNegotiation;
using Percolator.Apps.FileTransfer.Domain.Tests.TestDoubles;
using Percolator.Domain.Identities.ValueObjects;

namespace Percolator.Apps.FileTransfer.Domain.Tests;

[TestFixture]
public sealed class BitfieldTests
{
    [Test]
    public void DT_BIT_01_StructuralImmutability_BitfieldMutationsReturnNewInstancesAndEnforceBounds()
    {
        ChunkCount total = ChunkCount.FromInt(16);
        Bitfield initial = Bitfield.Empty(total);

        Bitfield mutated = initial.WithChunk(ChunkIndex.FromInt(5));

        initial.PopCount.Should().Be(0);
        initial.HasChunk(ChunkIndex.FromInt(5)).Should().BeFalse();

        mutated.PopCount.Should().Be(1);
        mutated.HasChunk(ChunkIndex.FromInt(5)).Should().BeTrue();

        Bitfield cleared = mutated.WithoutChunk(ChunkIndex.FromInt(5));
        cleared.PopCount.Should().Be(0);
        cleared.HasChunk(ChunkIndex.FromInt(5)).Should().BeFalse();
        mutated.PopCount.Should().Be(1);

        // Out-of-bounds mutation attempts must throw DomainValidationException
        Action actNegative = () => initial.WithChunk(ChunkIndex.FromInt(-1));
        actNegative.Should().Throw<DomainValidationException>();

        Action actOutOfRange = () => initial.WithChunk(ChunkIndex.FromInt(16));
        actOutOfRange.Should().Throw<DomainValidationException>();
    }

    [Test]
    public void DT_BIT_02_TrailingUnusedPaddingBits_NonZeroPaddingThrowsException()
    {
        // 9 chunks: requires 2 bytes.
        // Byte 0: 8 chunks (bits 0-7)
        // Byte 1: 1 chunk (bit 0 = 0x80), bits 1-7 (0x7F) are trailing unused padding.
        ChunkCount total = ChunkCount.FromInt(9);

        // Valid: bit 0 of byte 1 is set, padding bits are 0
        byte[] validBytes = [0xFF, 0x80];
        Bitfield valid = Bitfield.FromBytes(validBytes, total);
        valid.PopCount.Should().Be(9);

        // Invalid: bit 1 of byte 1 is set (padding bit 0x40 != 0)
        byte[] invalidBytes = [0xFF, 0xC0];
        Action act = () => Bitfield.FromBytes(invalidBytes, total);
        act.Should().Throw<DomainValidationException>()
            .WithMessage("*non-zero bits in trailing padding*");
    }

    [TestCase(1, 1)]
    [TestCase(7, 1)]
    [TestCase(8, 1)]
    [TestCase(9, 2)]
    [TestCase(15, 2)]
    [TestCase(16, 2)]
    public void DT_BIT_03_BoundaryBitfield_ByteLengthCalculations(int chunkCountInt, int expectedByteLength)
    {
        ChunkCount total = ChunkCount.FromInt(chunkCountInt);

        Bitfield empty = Bitfield.Empty(total);
        empty.ByteLength.Should().Be(expectedByteLength);
        empty.PopCount.Should().Be(0);

        Bitfield full = Bitfield.Full(total);
        full.ByteLength.Should().Be(expectedByteLength);
        full.PopCount.Should().Be(chunkCountInt);
        full.IsComplete.Should().BeTrue();

        // Roundtrip through FromBytes
        byte[] bytes = full.ToByteArray();
        bytes.Length.Should().Be(expectedByteLength);
        Bitfield roundtrip = Bitfield.FromBytes(bytes, total);
        roundtrip.PopCount.Should().Be(chunkCountInt);
        roundtrip.IsComplete.Should().BeTrue();
        roundtrip.Should().Be(full);
    }

    [Test]
    public void DT_BIT_04_BitfieldSetIntersectionAlgebra()
    {
        ChunkCount total = ChunkCount.FromInt(8);
        Bitfield a = Bitfield.Empty(total)
            .WithChunk(ChunkIndex.FromInt(0))
            .WithChunk(ChunkIndex.FromInt(1))
            .WithChunk(ChunkIndex.FromInt(2));

        Bitfield b = Bitfield.Empty(total)
            .WithChunk(ChunkIndex.FromInt(1))
            .WithChunk(ChunkIndex.FromInt(2))
            .WithChunk(ChunkIndex.FromInt(3));

        Bitfield intersection = a.Intersect(b);

        intersection.PopCount.Should().Be(2);
        intersection.HasChunk(ChunkIndex.FromInt(0)).Should().BeFalse();
        intersection.HasChunk(ChunkIndex.FromInt(1)).Should().BeTrue();
        intersection.HasChunk(ChunkIndex.FromInt(2)).Should().BeTrue();
        intersection.HasChunk(ChunkIndex.FromInt(3)).Should().BeFalse();
    }

    [Test]
    public void DT_BIT_05_BitfieldSetDifferenceAlgebra()
    {
        ChunkCount total = ChunkCount.FromInt(8);
        Bitfield other = Bitfield.Empty(total)
            .WithChunk(ChunkIndex.FromInt(1))
            .WithChunk(ChunkIndex.FromInt(2))
            .WithChunk(ChunkIndex.FromInt(3));

        Bitfield local = Bitfield.Empty(total)
            .WithChunk(ChunkIndex.FromInt(2));

        Bitfield needed = other.Difference(local);

        needed.PopCount.Should().Be(2);
        needed.HasChunk(ChunkIndex.FromInt(1)).Should().BeTrue();
        needed.HasChunk(ChunkIndex.FromInt(3)).Should().BeTrue();
        needed.HasChunk(ChunkIndex.FromInt(2)).Should().BeFalse();
    }

    [Test]
    public void DT_BIT_06_SelectiveDownloadCompletionEvaluation()
    {
        // 4 files, 1 MB each -> 4 chunks total
        TransferManifest manifest = DomainTestHelper.CreateTestManifest(fileCount: 4, bytesPerFile: 1024 * 1024);
        PublicIdentityId initiatorId = DomainTestHelper.CreateIdentity();
        FakeDateTimeProvider clock = new();

        TransferSession session = TransferSession.CreateIncomingStaged(
            TransferSessionId.New(),
            initiatorId,
            manifest,
            SeedRatio.Zero,
            clock);

        // Accept files 1 and 3 only
        SelectedFileIndices selection = SelectedFileIndices.FromIndices(
            [FileIndex.FromInt(1), FileIndex.FromInt(3)],
            manifest.TotalFiles);
        session.AcceptTransfer(selection, clock);

        session.IsDownloadComplete.Should().BeFalse();

        // Feed chunk 1
        byte[] chunk1Data = new byte[1024 * 1024];
        BitConverter.GetBytes(1).CopyTo(chunk1Data, 0);
        session.VerifyAndRecordChunk(ChunkIndex.FromInt(1), PlaintextChunkData.FromBytes(chunk1Data), clock);
        session.IsDownloadComplete.Should().BeFalse();

        // Feed chunk 3
        byte[] chunk3Data = new byte[1024 * 1024];
        BitConverter.GetBytes(3).CopyTo(chunk3Data, 0);
        session.VerifyAndRecordChunk(ChunkIndex.FromInt(3), PlaintextChunkData.FromBytes(chunk3Data), clock);

        // Assert: Download is complete even though chunks 0 and 2 are missing!
        session.IsDownloadComplete.Should().BeTrue();
        session.State.Should().Be(TransferSessionState.Completed);
    }

    [Test]
    public void DT_BIT_07_BitfieldMalformedByteLengthRejection()
    {
        // 10 chunks expects 2 bytes
        ChunkCount total = ChunkCount.FromInt(10);

        Action actTooShort = () => Bitfield.FromBytes([0xFF], total);
        actTooShort.Should().Throw<DomainValidationException>();

        Action actTooLong = () => Bitfield.FromBytes([0xFF, 0x00, 0x00], total);
        actTooLong.Should().Throw<DomainValidationException>();
    }

    [Test]
    public void DT_BIT_08_BitfieldAndSelectedFileIndices_ValueObjectStructuralEquality()
    {
        ChunkCount total = ChunkCount.FromInt(12);
        Bitfield bf1 = Bitfield.Empty(total).WithChunk(ChunkIndex.FromInt(2)).WithChunk(ChunkIndex.FromInt(9));
        Bitfield bf2 = Bitfield.Empty(total).WithChunk(ChunkIndex.FromInt(9)).WithChunk(ChunkIndex.FromInt(2));
        Bitfield bfDifferent = Bitfield.Empty(total).WithChunk(ChunkIndex.FromInt(2));

        (bf1 == bf2).Should().BeTrue();
        bf1.GetHashCode().Should().Be(bf2.GetHashCode());
        (bf1 == bfDifferent).Should().BeFalse();

        FileCount files = FileCount.FromInt(5);
        SelectedFileIndices sel1 = SelectedFileIndices.FromIndices([FileIndex.FromInt(0), FileIndex.FromInt(3)], files);
        SelectedFileIndices sel2 = SelectedFileIndices.FromIndices([FileIndex.FromInt(3), FileIndex.FromInt(0), FileIndex.FromInt(3)], files);
        SelectedFileIndices selDifferent = SelectedFileIndices.FromIndices([FileIndex.FromInt(0)], files);

        (sel1 == sel2).Should().BeTrue();
        sel1.GetHashCode().Should().Be(sel2.GetHashCode());
        (sel1 == selDifferent).Should().BeFalse();
    }
}

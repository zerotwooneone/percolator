using Percolator.Infrastructure.Serialization;

namespace Percolator.Infrastructure.Tests.Packaging;

using System;
using FluentAssertions;
using NUnit.Framework;
using Percolator.Apps.FileTransfer.Domain.TransferNegotiation;
using Percolator.Apps.FileTransfer.Features.TransferNegotiation;

using System.Collections.Generic;
using Percolator.Apps.FileTransfer.Domain.Common;
using Percolator.Apps.FileTransfer.Domain.ManifestManagement;
using Percolator.Domain.Common;

[TestFixture]
public sealed class ProtobufPayloadSerializerTests
{
    private readonly ProtobufPayloadSerializer _serializer = new();

    [Test]
    public void GenericPayloadSerialization_RoundtripsCorrectly()
    {
        var sessionId = TransferSessionId.FromGuid(Guid.NewGuid());
        var reason = TransferTerminationReason.SenderCancelled;
        var cancelPayload = new FileTransferCancelPayload(sessionId, reason);

        var bytes = _serializer.Serialize(cancelPayload);
        bytes.Length.Should().BeGreaterThan(0);

        var result = _serializer.Deserialize<FileTransferCancelPayload>(bytes);
        result.IsSuccess.Should().BeTrue();
        result.Value!.TransferSessionId.Should().Be(sessionId);
        result.Value.Reason.Should().Be(reason);
    }

    [Test]
    public void ProtobufPayloadSerializationRoundtrip_ManifestOfferAcceptCancel()
    {
        TransferSessionId sessionId = TransferSessionId.New();

        List<FileEntry> entries =
        [
            new FileEntry(FileIndex.FromInt(0), SanitizedRelativePath.Parse("docs/spec.pdf"), FileSizeBytes.FromBytes(1024 * 1024), BundleByteOffset.Zero, FileLastModifiedUtc.FromUnixSeconds(1700000000)),
            new FileEntry(FileIndex.FromInt(1), SanitizedRelativePath.Parse("docs/notes.txt"), FileSizeBytes.FromBytes(512 * 1024), BundleByteOffset.FromOffset(1024 * 1024), FileLastModifiedUtc.FromUnixSeconds(1700000100))
        ];

        ChunkHash leaf0 = MerkleTreeBuilder.HashLeaf(new byte[] { 0x01, 0x02 });
        ChunkHash leaf1 = MerkleTreeBuilder.HashLeaf(new byte[] { 0x03, 0x04 });
        MerkleTreeIndex tree = MerkleTreeBuilder.BuildFromLeaves([leaf0, leaf1]);

        TransferManifest manifest = TransferManifest.Create(
            sessionId,
            RootBundleName.Parse("ReleasePackage"),
            ChunkSizeBytes.Default,
            tree,
            entries);

        // 1. Manifest roundtrip
        ReadOnlyMemory<byte> manifestBytes = _serializer.Serialize(manifest);
        DomainResult<TransferManifest> manifestRoundtrip = _serializer.Deserialize<TransferManifest>(manifestBytes);
        manifestRoundtrip.IsSuccess.Should().BeTrue();
        manifestRoundtrip.Value.ManifestId.Should().Be(sessionId);
        manifestRoundtrip.Value.RootName.Should().Be(manifest.RootName);
        manifestRoundtrip.Value.MerkleTree.Should().Be(manifest.MerkleTree);
        manifestRoundtrip.Value.Files.Should().HaveCount(2);

        // 2. Offer roundtrip
        FileTransferOfferPayload offer = new(
            sessionId,
            TransferBundleSummary.Create(manifest.RootName, manifest.TotalSize, manifest.TotalFiles, manifest.ChunkSize, manifest.TotalChunks, manifest.MerkleTree.Root),
            ManifestDistribution.FromInline(InlineManifestData.FromSpan(manifestBytes.Span)),
            TransferKey.Generate(),
            BaseNonce.Generate(),
            StreamAuthToken.Generate(),
            new RelayRendezvousDescriptor(RelayPipeEndpointUri.Parse("https://relay.percolator.net/pipe"), RelayPipeToken.Generate()),
            [new DirectEndpointCandidate(EndpointHost.Parse("192.168.1.25"), PortNumber.FromInt(5200), DirectCandidateKind.DirectLan)]);

        ReadOnlyMemory<byte> offerBytes = _serializer.Serialize(offer);
        DomainResult<FileTransferOfferPayload> offerRoundtrip = _serializer.Deserialize<FileTransferOfferPayload>(offerBytes);
        offerRoundtrip.IsSuccess.Should().BeTrue();
        offerRoundtrip.Value.TransferSessionId.Should().Be(sessionId);
        offerRoundtrip.Value.Summary.Should().Be(offer.Summary);
        offerRoundtrip.Value.DirectCandidates.Should().ContainSingle();

        // 3. Accept roundtrip
        Bitfield resumptionBitfield = Bitfield.Empty(manifest.TotalChunks).WithChunk(ChunkIndex.FromInt(0));
        FileTransferAcceptPayload accept = new(
            sessionId,
            [new DirectEndpointCandidate(EndpointHost.Parse("192.168.1.30"), PortNumber.FromInt(5201), DirectCandidateKind.DirectLan)],
            TransportRoutingPreference.DirectPreferred,
            resumptionBitfield);

        ReadOnlyMemory<byte> acceptBytes = _serializer.Serialize(accept);
        DomainResult<FileTransferAcceptPayload> acceptRoundtrip = _serializer.Deserialize<FileTransferAcceptPayload>(acceptBytes);
        acceptRoundtrip.IsSuccess.Should().BeTrue();
        acceptRoundtrip.Value.TransferSessionId.Should().Be(sessionId);
        acceptRoundtrip.Value.ResumptionBitfield.Should().Be(resumptionBitfield);

        // 4. Cancel roundtrip
        FileTransferCancelPayload cancel = new(sessionId, TransferTerminationReason.SenderCancelled);
        ReadOnlyMemory<byte> cancelBytes = _serializer.Serialize(cancel);
        DomainResult<FileTransferCancelPayload> cancelRoundtrip = _serializer.Deserialize<FileTransferCancelPayload>(cancelBytes);
        cancelRoundtrip.IsSuccess.Should().BeTrue();
        cancelRoundtrip.Value.Should().Be(cancel);
    }
}

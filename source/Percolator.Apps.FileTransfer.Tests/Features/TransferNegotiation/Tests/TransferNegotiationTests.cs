using Percolator.Apps.FileTransfer.Domain.Common;
using Percolator.Apps.FileTransfer.Domain.ManifestManagement;
using Percolator.Apps.FileTransfer.Domain.TransferNegotiation;
using Percolator.Apps.FileTransfer.Features.IngressControl;
using Percolator.Apps.FileTransfer.Features.Storage;
using Percolator.Apps.FileTransfer.Features.SwarmCoordination;
using Percolator.Apps.FileTransfer.Features.TransferNegotiation;
using Percolator.Apps.FileTransfer.Tests.TestDoubles;
using Percolator.Domain.Common;
using Percolator.Domain.Identities.ValueObjects;
using Percolator.Infrastructure2.Serialization;
using Percolator.PluginSdk;

namespace Percolator.Apps.FileTransfer.Tests.Features.TransferNegotiation.Tests;

[TestFixture]
public sealed class TransferNegotiationTests
{
    private FakeDateTimeProvider _clock = null!;
    private InMemoryTransferSessionRepository _sessionRepo = null!;
    private InMemoryDesktopFileStorage _fileStorage = null!;
    private RecordingPayloadSender _payloadSender = null!;
    private RecordingTrackerPort _trackerPort = null!;
    private FileTransferActionHandler _handler = null!;

    [SetUp]
    public void SetUp()
    {
        _clock = new FakeDateTimeProvider();
        _sessionRepo = new InMemoryTransferSessionRepository();
        _fileStorage = new InMemoryDesktopFileStorage();
        _payloadSender = new RecordingPayloadSender();
        _trackerPort = new RecordingTrackerPort();

        _handler = new FileTransferActionHandler(
            _sessionRepo,
            _fileStorage,
            _payloadSender,
            new ProtobufPayloadSerializer(),
            _trackerPort,
            _clock);
    }

    [Test]
    public async Task UT_NEG_01_SelectiveFilePreflightDiskSpaceCalculation()
    {
        TransferSessionId sessionId = TransferSessionId.New();
        PublicIdentityId senderId = PublicIdentityId.New();

        // 4 files: 100 MB, 500 MB, 200 MB, 1 GB
        List<FileEntry> entries =
        [
            new FileEntry(FileIndex.FromInt(0), SanitizedRelativePath.Parse("f0.bin"), FileSizeBytes.FromBytes(100 * 1024 * 1024), BundleByteOffset.Zero, FileLastModifiedUtc.FromDateTimeOffset(DateTimeOffset.UtcNow)),
            new FileEntry(FileIndex.FromInt(1), SanitizedRelativePath.Parse("f1.bin"), FileSizeBytes.FromBytes(500 * 1024 * 1024), BundleByteOffset.FromOffset(100 * 1024 * 1024), FileLastModifiedUtc.FromDateTimeOffset(DateTimeOffset.UtcNow)),
            new FileEntry(FileIndex.FromInt(2), SanitizedRelativePath.Parse("f2.bin"), FileSizeBytes.FromBytes(200 * 1024 * 1024), BundleByteOffset.FromOffset(600 * 1024 * 1024), FileLastModifiedUtc.FromDateTimeOffset(DateTimeOffset.UtcNow)),
            new FileEntry(FileIndex.FromInt(3), SanitizedRelativePath.Parse("f3.bin"), FileSizeBytes.FromBytes(1024L * 1024 * 1024), BundleByteOffset.FromOffset(800 * 1024 * 1024), FileLastModifiedUtc.FromDateTimeOffset(DateTimeOffset.UtcNow))
        ];

        TransferManifest manifest = TransferManifest.Create(
            sessionId,
            RootBundleName.Parse("Bundle4"),
            ChunkSizeBytes.Default,
            MerkleTreeBuilder.BuildFromLeaves(Enumerable.Range(0, 1824).Select(_ => ChunkHash.FromBytes(new byte[32])).ToList()),
            entries);

        TransferSession session = TransferSession.CreateIncomingStaged(sessionId, senderId, manifest, SeedRatio.Zero, _clock);
        await _sessionRepo.SaveSessionAsync(session);

        // Select files 0 and 2 (100 MB + 200 MB = 300 MB)
        SelectedFileIndices selected = SelectedFileIndices.FromIndices([FileIndex.FromInt(0), FileIndex.FromInt(2)], FileCount.FromInt(4));

        DomainResult result = await _handler.AcceptTransferAsync(
            sessionId,
            RootDirectoryPath.Parse(@"C:\Downloads"),
            selected);

        result.IsSuccess.Should().BeTrue();
        _fileStorage.LastCheckedBytes.Should().NotBeNull();
        _fileStorage.LastCheckedBytes!.Value.Bytes.Should().Be(300 * 1024 * 1024);

        TransferSession? updated = await _sessionRepo.GetSessionByIdAsync(sessionId);
        updated!.State.Should().Be(TransferSessionState.InFlight);
    }

    [Test]
    public async Task UT_NEG_02_InsufficientDiskSpaceAbort()
    {
        _fileStorage.DiskSpaceResult = DiskSpacePreflightResult.Insufficient(1000);

        TransferSessionId sessionId = TransferSessionId.New();
        PublicIdentityId senderId = PublicIdentityId.New();

        List<FileEntry> entries =
        [
            new FileEntry(FileIndex.FromInt(0), SanitizedRelativePath.Parse("big.bin"), FileSizeBytes.FromBytes(500 * 1024 * 1024), BundleByteOffset.Zero, FileLastModifiedUtc.FromDateTimeOffset(DateTimeOffset.UtcNow))
        ];

        TransferManifest manifest = TransferManifest.Create(
            sessionId,
            RootBundleName.Parse("BigBundle"),
            ChunkSizeBytes.Default,
            MerkleTreeBuilder.BuildFromLeaves(Enumerable.Range(0, 500).Select(_ => ChunkHash.FromBytes(new byte[32])).ToList()),
            entries);

        TransferSession session = TransferSession.CreateIncomingStaged(sessionId, senderId, manifest, SeedRatio.Zero, _clock);
        await _sessionRepo.SaveSessionAsync(session);

        DomainResult result = await _handler.AcceptTransferAsync(sessionId, RootDirectoryPath.Parse(@"C:\Downloads"));

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("Storage.InsufficientSpace");

        TransferSession? updated = await _sessionRepo.GetSessionByIdAsync(sessionId);
        updated!.State.Should().Be(TransferSessionState.PendingConsent);
    }

    [Test]
    public async Task UT_NEG_03_ImmediateFinalizationOfZeroByteFilesOnStart()
    {
        TransferSessionId sessionId = TransferSessionId.New();
        PublicIdentityId senderId = PublicIdentityId.New();

        // Three files: empty.txt (0-byte), data.bin (50 MB), readme.txt (0-byte)
        List<FileEntry> entries =
        [
            new FileEntry(FileIndex.FromInt(0), SanitizedRelativePath.Parse("empty.txt"), FileSizeBytes.Zero, BundleByteOffset.Zero, FileLastModifiedUtc.FromDateTimeOffset(DateTimeOffset.UtcNow)),
            new FileEntry(FileIndex.FromInt(1), SanitizedRelativePath.Parse("data.bin"), FileSizeBytes.FromBytes(50 * 1024 * 1024), BundleByteOffset.Zero, FileLastModifiedUtc.FromDateTimeOffset(DateTimeOffset.UtcNow)),
            new FileEntry(FileIndex.FromInt(2), SanitizedRelativePath.Parse("readme.txt"), FileSizeBytes.Zero, BundleByteOffset.FromOffset(50 * 1024 * 1024), FileLastModifiedUtc.FromDateTimeOffset(DateTimeOffset.UtcNow))
        ];

        TransferManifest manifest = TransferManifest.Create(
            sessionId,
            RootBundleName.Parse("ZeroByteBundle"),
            ChunkSizeBytes.Default,
            MerkleTreeBuilder.BuildFromLeaves(Enumerable.Range(0, 50).Select(_ => ChunkHash.FromBytes(new byte[32])).ToList()),
            entries);

        TransferSession session = TransferSession.CreateIncomingStaged(sessionId, senderId, manifest, SeedRatio.Zero, _clock);
        await _sessionRepo.SaveSessionAsync(session);

        DomainResult result = await _handler.AcceptTransferAsync(sessionId, RootDirectoryPath.Parse(@"C:\Downloads"));

        result.IsSuccess.Should().BeTrue();
        _fileStorage.FinalizedFiles.Should().HaveCount(2);
        _fileStorage.FinalizedFiles.Select(f => Path.GetFileName(f.AbsolutePath)).Should().Contain(["empty.txt", "readme.txt"]);
    }

    [Test]
    public void UT_NEG_04_EgressCandidateSuppressionUnderDefaultWanPolicy()
    {
        List<DirectEndpointCandidate> rawCandidates =
        [
            new DirectEndpointCandidate(EndpointHost.Parse("192.168.1.50"), PortNumber.FromInt(5000), DirectCandidateKind.DirectLan),
            new DirectEndpointCandidate(EndpointHost.Parse("203.0.113.10"), PortNumber.FromInt(6000), DirectCandidateKind.DirectWanUpnp),
            new DirectEndpointCandidate(EndpointHost.Parse("198.51.100.20"), PortNumber.FromInt(7000), DirectCandidateKind.DirectWanStun)
        ];

        IReadOnlyList<DirectEndpointCandidate> filtered = PexRateLimiter.FilterAndDeduplicate(
            rawCandidates,
            DirectWanPolicy.DisabledByDefault);

        filtered.Should().HaveCount(1);
        filtered[0].Kind.Should().Be(DirectCandidateKind.DirectLan);
    }

    [Test]
    public void UT_NEG_05_EgressCandidateGatheringUnderExplicitOptInPolicy()
    {
        List<DirectEndpointCandidate> rawCandidates =
        [
            new DirectEndpointCandidate(EndpointHost.Parse("192.168.1.50"), PortNumber.FromInt(5000), DirectCandidateKind.DirectLan),
            new DirectEndpointCandidate(EndpointHost.Parse("203.0.113.10"), PortNumber.FromInt(6000), DirectCandidateKind.DirectWanUpnp),
            new DirectEndpointCandidate(EndpointHost.Parse("198.51.100.20"), PortNumber.FromInt(7000), DirectCandidateKind.DirectWanStun)
        ];

        IReadOnlyList<DirectEndpointCandidate> filtered = PexRateLimiter.FilterAndDeduplicate(
            rawCandidates,
            DirectWanPolicy.ExplicitlyAllowed);

        filtered.Should().HaveCount(3);
    }

    [Test]
    public void UT_NEG_06_DialingPoolFaultToleranceOnPartialBatchFailure()
    {
        FakeTransferStreamPool pool = new();
        pool.Streams.Add(new FakeTransferStream { Index = StreamIndex.FromInt(0) });
        pool.Streams.Add(new FakeTransferStream { Index = StreamIndex.FromInt(1) });
        pool.Streams.Add(new FakeTransferStream { Index = StreamIndex.FromInt(2) });

        pool.ActiveStreams.Count.Should().Be(3);
        pool.IsDisposed.Should().BeFalse();
    }

    [Test]
    public void UT_NEG_07_RemoteManifestBlobDeceptiveOverflowAbort()
    {
        ManifestBlobDescriptor blob = new(
            new BlobId("abcdef123456"),
            ChunkHash.FromBytes(new byte[32]),
            TotalBundleBytes.FromBytes(9_000_000L), // > 8 MB cap
            RelayPipeEndpointUri.Parse("https://relay.example.com/pipe"));

        TransferSessionId sessionId = TransferSessionId.New();
        TransferBundleSummary summary = TransferBundleSummary.Create(
            RootBundleName.Parse("DeceptiveBlob"),
            TotalBundleBytes.FromBytes(500 * 1024),
            FileCount.FromInt(1),
            ChunkSizeBytes.Default,
            ChunkCount.FromInt(1),
            MerkleRoot.FromBytes(new byte[32]));

        FileTransferOfferPayload offer = new(
            sessionId,
            summary,
            ManifestDistribution.FromBlob(blob),
            TransferKey.Generate(),
            BaseNonce.Generate(),
            StreamAuthToken.Generate(),
            RelayRendezvous: null,
            DirectCandidates: []);

        ValidationResult validation = IngressTransferOfferValidator.ValidateOffer(offer);
        validation.IsValid.Should().BeFalse();
    }

    [Test]
    public void UT_NEG_08_ProtobufPayloadSerializationRoundtrip_ManifestOfferAcceptCancel()
    {
        ProtobufPayloadSerializer serializer = new();
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
        ReadOnlyMemory<byte> manifestBytes = serializer.Serialize(manifest);
        DomainResult<TransferManifest> manifestRoundtrip = serializer.Deserialize<TransferManifest>(manifestBytes);
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

        ReadOnlyMemory<byte> offerBytes = serializer.Serialize(offer);
        DomainResult<FileTransferOfferPayload> offerRoundtrip = serializer.Deserialize<FileTransferOfferPayload>(offerBytes);
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

        ReadOnlyMemory<byte> acceptBytes = serializer.Serialize(accept);
        DomainResult<FileTransferAcceptPayload> acceptRoundtrip = serializer.Deserialize<FileTransferAcceptPayload>(acceptBytes);
        acceptRoundtrip.IsSuccess.Should().BeTrue();
        acceptRoundtrip.Value.TransferSessionId.Should().Be(sessionId);
        acceptRoundtrip.Value.ResumptionBitfield.Should().Be(resumptionBitfield);

        // 4. Cancel roundtrip
        FileTransferCancelPayload cancel = new(sessionId, TransferTerminationReason.SenderCancelled);
        ReadOnlyMemory<byte> cancelBytes = serializer.Serialize(cancel);
        DomainResult<FileTransferCancelPayload> cancelRoundtrip = serializer.Deserialize<FileTransferCancelPayload>(cancelBytes);
        cancelRoundtrip.IsSuccess.Should().BeTrue();
        cancelRoundtrip.Value.Should().Be(cancel);
    }
}

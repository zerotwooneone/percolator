using System.Security.Cryptography;
using Percolator.Apps.FileTransfer.Domain.Common;
using Percolator.Apps.FileTransfer.Domain.ManifestManagement;
using Percolator.Apps.FileTransfer.Domain.TransferNegotiation;
using Percolator.Apps.FileTransfer.Features.IngressControl;
using Percolator.Apps.FileTransfer.Features.Runtime;
using Percolator.Apps.FileTransfer.Features.Storage;
using Percolator.Apps.FileTransfer.Features.SwarmCoordination;
using Percolator.Apps.FileTransfer.Features.TransferNegotiation;
using Percolator.Apps.FileTransfer.Ports;
using Percolator.Apps.FileTransfer.Tests.TestDoubles;
using Percolator.Domain.Channels.ValueObjects;
using Percolator.Domain.Common;
using Percolator.Domain.Identities.ValueObjects;
using Percolator.PluginSdk;
using PublicIdentityId = Percolator.Domain.Identities.PublicIdentityId;

namespace Percolator.Apps.FileTransfer.Tests.Features.TransferNegotiation.Tests;

[TestFixture]
public sealed class TransferNegotiationTests
{
    private FakeDateTimeProvider _clock = null!;
    private InMemoryTransferSessionRepository _sessionRepo = null!;
    private InMemoryDesktopFileStorage _fileStorage = null!;
    private RecordingPayloadSender _payloadSender = null!;
    private RecordingTrackerPort _trackerPort = null!;
    private InMemoryFilterPolicyRepository _policyRepository = null!;
    private InMemoryTransferSessionCertificateStore _certStore = null!;
    private InMemoryTransferSessionTransportStore _transportStore = null!;
    private TransferSessionRuntime _runtime = null!;
    private TestPayloadSerializer _serializer = null!;
    private FileTransferActionHandler _handler = null!;

    [SetUp]
    public void SetUp()
    {
        _clock = new FakeDateTimeProvider();
        _sessionRepo = new InMemoryTransferSessionRepository();
        _transportStore = new InMemoryTransferSessionTransportStore();
        _fileStorage = new InMemoryDesktopFileStorage();
        _payloadSender = new RecordingPayloadSender();
        _trackerPort = new RecordingTrackerPort();
        _policyRepository = new InMemoryFilterPolicyRepository();
        _certStore = new InMemoryTransferSessionCertificateStore();
        _serializer = new TestPayloadSerializer();
        _runtime = new TransferSessionRuntime(
            new FakeTransferPeerTransport(),
            _certStore,
            _trackerPort,
            _fileStorage,
            new RecordingTransferRateLimiter(),
            _clock,
            new InMemoryEventBusForTests(),
            _serializer,
            new DirectBackgroundPayloadDispatcher(_payloadSender),
            new InMemoryTransferSessionCheckpointStore(),
            new TransferSessionRuntimeOptions());
        var defaultFilter = new FileTransferIngressFilter(_policyRepository) { IsEnabled = true };

        _handler = new FileTransferActionHandler(
            _sessionRepo,
            _fileStorage,
            _payloadSender,
            _serializer,
            _trackerPort,
            _clock,
            defaultFilter,
            new InMemoryBlobStorageService(),
            new FakeNetworkEndpointDiscoveryPort(),
            _runtime,
            _certStore,
            _transportStore);
    }

    [TearDown]
    public async Task TearDown()
    {
        await _runtime.DisposeAsync();
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
        await _transportStore.SaveTransportContextAsync(new TransferSessionTransportContext(
            sessionId,
            ChannelId.New(),
            PublicIdentityId.New(),
            SwarmRendezvousId.Generate(),
            EphemeralSwarmPeerId.Generate(),
            StreamAuthToken.Generate(),
            DirectWanPolicy.DisabledByDefault,
            RelayRendezvous: null,
            DirectCandidates: []));

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
        await _transportStore.SaveTransportContextAsync(new TransferSessionTransportContext(
            sessionId,
            ChannelId.New(),
            PublicIdentityId.New(),
            SwarmRendezvousId.Generate(),
            EphemeralSwarmPeerId.Generate(),
            StreamAuthToken.Generate(),
            DirectWanPolicy.DisabledByDefault,
            RelayRendezvous: null,
            DirectCandidates: []));

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
    public async Task UT_NEG_06_InitiateTransfer_UnderDefaultWanPolicy_DropsPublicWanCandidatesFromOffer()
    {
        ChannelId channelId = ChannelId.New();
        PublicIdentityId initiatorId = PublicIdentityId.New();
        PublicIdentityId recipientId = PublicIdentityId.New();

        RootDirectoryPath sourceRoot = RootDirectoryPath.Parse(@"C:\SourceCandidates");
        SanitizedRelativePath relPath = SanitizedRelativePath.Parse("file.bin");
        ResolvedFilePath fullPath = ResolvedFilePath.Resolve(sourceRoot, relPath);
        _fileStorage.FilesOnDisk[fullPath.AbsolutePath] = new byte[1024];

        FakeNetworkEndpointDiscoveryPort discoveryPort = new();
        discoveryPort.LocalCandidates.Clear();
        discoveryPort.LocalCandidates.Add(new DirectEndpointCandidate(
            EndpointHost.Parse("192.168.1.50"),
            PortNumber.FromInt(5000),
            DirectCandidateKind.DirectLan));
        discoveryPort.LocalCandidates.Add(new DirectEndpointCandidate(
            EndpointHost.Parse("203.0.113.10"),
            PortNumber.FromInt(6000),
            DirectCandidateKind.DirectWanUpnp));
        discoveryPort.LocalCandidates.Add(new DirectEndpointCandidate(
            EndpointHost.Parse("198.51.100.20"),
            PortNumber.FromInt(7000),
            DirectCandidateKind.DirectLan)); // Public IP mislabeled as DirectLan

        FileTransferActionHandler handler = new(
            _sessionRepo,
            _fileStorage,
            _payloadSender,
            _serializer,
            _trackerPort,
            _clock,
            new FileTransferIngressFilter(_policyRepository) { IsEnabled = true },
            new InMemoryBlobStorageService(),
            discoveryPort,
            _runtime,
            _certStore,
            _transportStore);

        DomainResult<TransferSessionId> result = await handler.InitiateTransferAsync(
            channelId,
            initiatorId,
            [recipientId],
            sourceRoot,
            [relPath],
            DirectWanPolicy.DisabledByDefault);

        result.IsSuccess.Should().BeTrue();
        _payloadSender.SentPayloads.Should().ContainSingle();

        var offer = _serializer.Deserialize<FileTransferOfferPayload>(
            _payloadSender.SentPayloads[0].Payload.Slice(1)).Value;

        // Must ONLY contain the private LAN candidate; public IPs (even misclassified ones) must be stripped!
        offer.DirectCandidates.Should().ContainSingle();
        offer.DirectCandidates[0].Host.Value.Should().Be("192.168.1.50");
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
            SwarmRendezvousId.Generate(),
            StreamAuthToken.Generate(),
            RelayRendezvous: null,
            DirectCandidates: []);

        ValidationResult validation = IngressTransferOfferValidator.ValidateOffer(offer);
        validation.IsValid.Should().BeFalse();
    }

    [Test]
    public async Task TransferActions_WhenIngressFilterDisabled_RejectsInitiateAndAccept()
    {
        var filter = new FileTransferIngressFilter(_policyRepository);
        filter.IsEnabled.Should().BeFalse();
        FileTransferIngressFilter.PrivacyNotice.Should().Contain("Direct P2P file transfer uses multi-stream TCP connections");

        var handler = new FileTransferActionHandler(
            _sessionRepo,
            _fileStorage,
            _payloadSender,
            new TestPayloadSerializer(),
            _trackerPort,
            _clock,
            filter,
            new InMemoryBlobStorageService(),
            new FakeNetworkEndpointDiscoveryPort(),
            _runtime,
            _certStore,
            _transportStore);

        var initiateResult = await handler.InitiateTransferAsync(
            ChannelId.New(),
            PublicIdentityId.New(),
            [PublicIdentityId.New()],
            RootDirectoryPath.Parse(@"C:\Source"),
            [SanitizedRelativePath.Parse("test.bin")],
            DirectWanPolicy.DisabledByDefault);

        initiateResult.IsSuccess.Should().BeFalse();
        initiateResult.Error.Code.Should().Be(FileTransferIngressFilter.PolicyDisabledErrorCode);
        initiateResult.Error.Description.Should().Be(FileTransferIngressFilter.PrivacyNotice);

        var acceptResult = await handler.AcceptTransferAsync(
            TransferSessionId.New(),
            RootDirectoryPath.Parse(@"C:\Downloads"));

        acceptResult.IsSuccess.Should().BeFalse();
        acceptResult.Error.Code.Should().Be(FileTransferIngressFilter.PolicyDisabledErrorCode);
        acceptResult.Error.Description.Should().Be(FileTransferIngressFilter.PrivacyNotice);
    }

    [Test]
    public async Task TransferActions_WhenIngressFilterEnabled_PassesPrivacyPolicyCheck()
    {
        var filter = new FileTransferIngressFilter(_policyRepository)
        {
            IsEnabled = true
        };

        var handler = new FileTransferActionHandler(
            _sessionRepo,
            _fileStorage,
            _payloadSender,
            new TestPayloadSerializer(),
            _trackerPort,
            _clock,
            filter,
            new InMemoryBlobStorageService(),
            new FakeNetworkEndpointDiscoveryPort(),
            _runtime,
            _certStore,
            _transportStore);

        var initiateResult = await handler.InitiateTransferAsync(
            ChannelId.New(),
            PublicIdentityId.New(),
            [],
            RootDirectoryPath.Parse(@"C:\Source"),
            [SanitizedRelativePath.Parse("test.bin")],
            DirectWanPolicy.DisabledByDefault);

        initiateResult.IsSuccess.Should().BeFalse();
        initiateResult.Error.Code.Should().Be("Transfer.NoRecipients");
    }

    [Test]
    public async Task InitiateTransferAsync_FileSequentialParallelHashing_ProducesByteIdenticalMerkleTreeAndReportsProgress()
    {
        var root = RootDirectoryPath.Parse(@"C:\TestBundleSource");
        var channelId = ChannelId.New();
        var initiatorId = PublicIdentityId.New();
        var recipientId = PublicIdentityId.New();

        // 1,000 tiny files + 0-byte files + larger boundary-spanning files
        const int tinyFileCount = 1000;
        var relativePaths = new List<SanitizedRelativePath>();
        var allBytes = new MemoryStream();

        // 0-byte file at the beginning
        var emptyPath1 = SanitizedRelativePath.Parse("zero1.dat");
        relativePaths.Add(emptyPath1);
        _fileStorage.FilesOnDisk[ResolvedFilePath.Resolve(root, emptyPath1).AbsolutePath] = [];

        for (int i = 0; i < tinyFileCount; i++)
        {
            var rel = SanitizedRelativePath.Parse($"node_modules/pkg_{i % 50}/file_{i}.js");
            relativePaths.Add(rel);
            byte[] content = System.Text.Encoding.UTF8.GetBytes($"console.log('file {i}');\n");
            _fileStorage.FilesOnDisk[ResolvedFilePath.Resolve(root, rel).AbsolutePath] = content;
            allBytes.Write(content);
        }

        // Another 0-byte file in the middle
        var emptyPath2 = SanitizedRelativePath.Parse("zero2.dat");
        relativePaths.Add(emptyPath2);
        _fileStorage.FilesOnDisk[ResolvedFilePath.Resolve(root, emptyPath2).AbsolutePath] = [];

        // Larger file spanning chunk boundaries
        var largePath = SanitizedRelativePath.Parse("assets/large_asset.bin");
        relativePaths.Add(largePath);
        byte[] largeContent = new byte[150_000];
        RandomNumberGenerator.Fill(largeContent);
        _fileStorage.FilesOnDisk[ResolvedFilePath.Resolve(root, largePath).AbsolutePath] = largeContent;
        allBytes.Write(largeContent);

        byte[] fullContiguous = allBytes.ToArray();
        TotalBundleBytes totalBytes = TotalBundleBytes.FromBytes(fullContiguous.Length);
        ChunkSizeBytes chunkSize = ChunkSizeBytes.SelectOptimalForBundle(totalBytes);
        int expectedChunkCount = (int)Math.Max(1, (fullContiguous.Length + chunkSize.Bytes - 1) / chunkSize.Bytes);

        var expectedLeaves = new List<ChunkHash>();
        for (int c = 0; c < expectedChunkCount; c++)
        {
            int offset = c * chunkSize.Bytes;
            int len = Math.Min(chunkSize.Bytes, fullContiguous.Length - offset);
            expectedLeaves.Add(MerkleTreeBuilder.HashLeaf(fullContiguous.AsSpan(offset, len)));
        }
        var expectedMerkleTree = MerkleTreeBuilder.BuildFromLeaves(expectedLeaves);

        var reportedProgress = new List<TransferPreparationProgress>();
        var progressHandler = new SynchronousProgress<TransferPreparationProgress>(p =>
        {
            lock (reportedProgress)
            {
                reportedProgress.Add(p);
            }
        });

        var result = await _handler.InitiateTransferAsync(
            channelId,
            initiatorId,
            [recipientId],
            root,
            relativePaths,
            DirectWanPolicy.ExplicitlyAllowed,
            preparationProgress: progressHandler);

        result.IsSuccess.Should().BeTrue();
        TransferSession? session = await _sessionRepo.GetSessionByIdAsync(result.Value);
        session.Should().NotBeNull();
        session!.Manifest.TotalFiles.Count.Should().Be(relativePaths.Count);
        session.Manifest.TotalChunks.Count.Should().Be(expectedChunkCount);
        session.Manifest.MerkleTree.Root.Should().Be(expectedMerkleTree.Root);

        for (int i = 0; i < expectedChunkCount; i++)
        {
            session.Manifest.MerkleTree.Leaves[i].Should().Be(expectedLeaves[i]);
        }

        reportedProgress.Should().NotBeEmpty();
        var lastProgress = reportedProgress[^1];
        lastProgress.FilesScanned.Should().Be(relativePaths.Count);
        lastProgress.TotalFiles.Should().Be(relativePaths.Count);
        lastProgress.ChunksHashed.Should().Be(expectedChunkCount);
        lastProgress.TotalChunks.Should().Be(expectedChunkCount);
    }

    private sealed class SynchronousProgress<T> : IProgress<T>
    {
        private readonly Action<T> _handler;
        public SynchronousProgress(Action<T> handler) => _handler = handler;
        public void Report(T value) => _handler(value);
    }
}

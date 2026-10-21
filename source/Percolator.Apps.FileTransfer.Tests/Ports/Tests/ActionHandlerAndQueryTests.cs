using Percolator.Apps.FileTransfer.Domain.Common;
using Percolator.Apps.FileTransfer.Domain.ManifestManagement;
using Percolator.Apps.FileTransfer.Domain.SwarmCoordination;
using Percolator.Apps.FileTransfer.Domain.TransferNegotiation;
using Percolator.Apps.FileTransfer.Features.IngressControl;
using Percolator.Apps.FileTransfer.Features.TransferNegotiation;
using Percolator.Apps.FileTransfer.Ports;
using Percolator.Apps.FileTransfer.Tests.TestDoubles;
using Percolator.Domain.Channels.ValueObjects;
using Percolator.Domain.Common;
using Percolator.Domain.Identities.ValueObjects;
using Percolator.Infrastructure2.Serialization;
using Percolator.PluginSdk;

namespace Percolator.Apps.FileTransfer.Tests.Ports.Tests;

[TestFixture]
public sealed class ActionHandlerAndQueryTests
{
    private FakeDateTimeProvider _clock = null!;
    private InMemoryTransferSessionRepository _sessionRepo = null!;
    private InMemoryDesktopFileStorage _fileStorage = null!;
    private RecordingPayloadSender _payloadSender = null!;
    private RecordingTrackerPort _trackerPort = null!;
    private InMemoryBlobStorageService _blobStorage = null!;
    private FakeNetworkEndpointDiscoveryPort _endpointDiscovery = null!;
    private FakeTransferStreamPool _streamPool = null!;
    private ProtobufPayloadSerializer _serializer = null!;
    private FileTransferActionHandler _handler = null!;
    private FileTransferQueryService _queryService = null!;
    private TransferOfferReaperService _reaperService = null!;

    [SetUp]
    public void SetUp()
    {
        _clock = new FakeDateTimeProvider();
        _sessionRepo = new InMemoryTransferSessionRepository();
        _fileStorage = new InMemoryDesktopFileStorage();
        _payloadSender = new RecordingPayloadSender();
        _trackerPort = new RecordingTrackerPort();
        _blobStorage = new InMemoryBlobStorageService();
        _endpointDiscovery = new FakeNetworkEndpointDiscoveryPort();
        _streamPool = new FakeTransferStreamPool();
        _serializer = new ProtobufPayloadSerializer();

        _handler = new FileTransferActionHandler(
            _sessionRepo,
            _fileStorage,
            _payloadSender,
            _serializer,
            _trackerPort,
            _clock,
            _blobStorage,
            _endpointDiscovery,
            _streamPool);

        _queryService = new FileTransferQueryService(_sessionRepo);
        _reaperService = new TransferOfferReaperService(_sessionRepo, _clock);
    }

    [TearDown]
    public async Task TearDown()
    {
        await _streamPool.DisposeAsync();
    }

    [Test]
    public async Task UT_CMD_01_InitiateTransferBoundsEnforcement()
    {
        // 50,001 files (exceeds 50,000 maximum allowed)
        List<SanitizedRelativePath> tooManyFiles = [];
        for (int i = 0; i < 50_001; i++)
        {
            tooManyFiles.Add(SanitizedRelativePath.Parse($"f{i}.bin"));
        }

        PublicIdentityId recipient = PublicIdentityId.New();

        DomainResult<TransferSessionId> result = await _handler.InitiateTransferAsync(
            [recipient],
            RootDirectoryPath.Parse(@"C:\Files"),
            tooManyFiles,
            DirectWanPolicy.DisabledByDefault);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("Transfer.FileLimitExceeded");

        IReadOnlyList<TransferSession> sessions = await _sessionRepo.GetActiveSessionsAsync();
        sessions.Should().BeEmpty();
    }

    [Test]
    public async Task UT_CMD_02_ProactiveTrackerEndpointUnregistrationOnCancellation()
    {
        TransferSessionId sessionId = TransferSessionId.New();
        PublicIdentityId initiator = PublicIdentityId.New();
        PublicIdentityId recipient = PublicIdentityId.New();

        TransferManifest manifest = TransferManifest.Create(
            sessionId,
            RootBundleName.Parse("TrackerCancelTest"),
            ChunkSizeBytes.Default,
            MerkleTreeBuilder.BuildFromLeaves([ChunkHash.FromBytes(new byte[32])]),
            [new FileEntry(FileIndex.FromInt(0), SanitizedRelativePath.Parse("test.bin"), FileSizeBytes.FromBytes(100), BundleByteOffset.Zero, FileLastModifiedUtc.FromDateTimeOffset(DateTimeOffset.UtcNow))]);

        TransferSession session = TransferSession.CreateOutgoing1to1(sessionId, initiator, recipient, manifest, SeedRatio.Zero, _clock);
        await _sessionRepo.SaveSessionAsync(session);

        DomainResult cancelResult = await _handler.CancelTransferAsync(sessionId, TransferTerminationReason.UserDeclined);

        cancelResult.IsSuccess.Should().BeTrue();
        session.State.Should().Be(TransferSessionState.Cancelled);
        _trackerPort.Unregistered.Should().ContainSingle(u => u.MemberId == initiator);
    }

    [Test]
    public async Task UT_CMD_03_InitiateTransferEndToEndWithChannelAndDistinctIdentities()
    {
        ChannelId channelId = ChannelId.New();
        PublicIdentityId localInitiator = PublicIdentityId.New();
        PublicIdentityId recipient = PublicIdentityId.New();
        RootDirectoryPath sourceRoot = RootDirectoryPath.Parse(@"C:\Bundle");
        SanitizedRelativePath relPath = SanitizedRelativePath.Parse("archive.bin");

        ResolvedFilePath fullPath = ResolvedFilePath.Resolve(sourceRoot, relPath);
        byte[] content = new byte[4096];
        Random.Shared.NextBytes(content);
        _fileStorage.FilesOnDisk[fullPath.AbsolutePath] = content;

        DomainResult<TransferSessionId> initResult = await _handler.InitiateTransferAsync(
            channelId,
            localInitiator,
            [recipient],
            sourceRoot,
            [relPath],
            DirectWanPolicy.DisabledByDefault);

        initResult.IsSuccess.Should().BeTrue();
        TransferSessionId sessionId = initResult.Value;

        // Verify OutboundPayloadContext carries real ChannelId and distinct Sender/Recipient
        _payloadSender.SentPayloads.Should().ContainSingle();
        OutboundPayloadContext sentOffer = _payloadSender.SentPayloads[0];
        sentOffer.ChannelId.Should().Be(channelId);
        sentOffer.SenderIdentityId.Should().Be(localInitiator);
        sentOffer.RecipientIdentityId.Should().Be(recipient);
        sentOffer.AppId.Should().Be(AppId.FileTransferControl);

        // Verify transport context was persisted with session encryption key and tracker announced Seeder role
        TransferSessionTransportContext? transportCtx = await _sessionRepo.GetTransportContextAsync(sessionId);
        transportCtx.Should().NotBeNull();
        SwarmRendezvousId expectedRendezvous = SwarmRendezvousId.Derive(transportCtx!.SessionEncryptionKey);
        _trackerPort.Announced.Should().ContainSingle(a => a.Rendezvous == expectedRendezvous && a.MemberId == localInitiator && a.Role == SwarmPeerRole.Seeder);

        // Cancel and verify tracker unregisters deterministic SwarmRendezvousId derived from session key
        DomainResult cancelResult = await _handler.CancelTransferAsync(sessionId, TransferTerminationReason.SenderCancelled);
        cancelResult.IsSuccess.Should().BeTrue();
        _trackerPort.Unregistered.Should().ContainSingle(u => u.Rendezvous == expectedRendezvous && u.MemberId == localInitiator);
    }

    [Test]
    public void UT_CMD_04_AdaptiveChunkSizingScalesWithBundleSize()
    {
        ChunkSizeBytes.SelectOptimalForBundle(TotalBundleBytes.FromBytes(10L * 1024 * 1024)).Bytes.Should().Be(262_144);          // 10 MB -> 256 KB
        ChunkSizeBytes.SelectOptimalForBundle(TotalBundleBytes.FromBytes(500L * 1024 * 1024)).Bytes.Should().Be(1_048_576);       // 500 MB -> 1 MB
        ChunkSizeBytes.SelectOptimalForBundle(TotalBundleBytes.FromBytes(4L * 1024 * 1024 * 1024)).Bytes.Should().Be(2_097_152);  // 4 GB -> 2 MB
        ChunkSizeBytes.SelectOptimalForBundle(TotalBundleBytes.FromBytes(100L * 1024 * 1024 * 1024)).Bytes.Should().Be(4_194_304);// 100 GB -> 4 MB
    }

    [Test]
    public async Task UT_CMD_05_LargeManifestUploadsToBlobStorageAndResolvesOnRecipient()
    {
        ChannelId channelId = ChannelId.New();
        PublicIdentityId senderId = PublicIdentityId.New();
        PublicIdentityId recipientId = PublicIdentityId.New();
        RootDirectoryPath sourceRoot = RootDirectoryPath.Parse(@"C:\LargeManifestBundle");

        // Create 1,200 small files so serialized manifest exceeds 48 KB inline threshold
        List<SanitizedRelativePath> files = new(1200);
        for (int i = 0; i < 1200; i++)
        {
            var rel = SanitizedRelativePath.Parse($"folder_{i / 50}/document_item_{i:D4}.txt");
            files.Add(rel);
            ResolvedFilePath abs = ResolvedFilePath.Resolve(sourceRoot, rel);
            _fileStorage.FilesOnDisk[abs.AbsolutePath] = new byte[128];
        }

        DomainResult<TransferSessionId> initResult = await _handler.InitiateTransferAsync(
            channelId,
            senderId,
            [recipientId],
            sourceRoot,
            files,
            DirectWanPolicy.DisabledByDefault);

        initResult.IsSuccess.Should().BeTrue();
        _payloadSender.SentPayloads.Should().ContainSingle();

        // Dispatch the offer payload on the recipient's FileTransferPayloadHandler
        InMemoryTransferSessionRepository recipientRepo = new();
        InMemoryFilterPolicyRepository policyRepo = new();
        var payloadHandler = new FileTransferPayloadHandler(
            recipientRepo,
            policyRepo,
            _serializer,
            _clock,
            _blobStorage);

        var inboundCtx = new InboundPayloadContext(
            channelId,
            senderId,
            DeviceId.Primary,
            AppId.FileTransferControl,
            _payloadSender.SentPayloads[0].Payload,
            _clock.UtcNow);

        DomainResult handleResult = await payloadHandler.HandleInboundAsync(inboundCtx);
        handleResult.IsSuccess.Should().BeTrue();

        TransferSession? stagedSession = await recipientRepo.GetSessionByIdAsync(initResult.Value);
        stagedSession.Should().NotBeNull();
        stagedSession!.Manifest.TotalFiles.Count.Should().Be(1200);
        stagedSession.State.Should().Be(TransferSessionState.PendingConsent);
    }

    [Test]
    public async Task UT_CMD_06_CrocResumptionBitfieldAndDirectToRelayFallback()
    {
        ChannelId channelId = ChannelId.New();
        PublicIdentityId senderId = PublicIdentityId.New();
        RootDirectoryPath sourceRoot = RootDirectoryPath.Parse(@"C:\SourceMultiChunk");
        SanitizedRelativePath relPath = SanitizedRelativePath.Parse("movie.mkv");

        ResolvedFilePath fullPath = ResolvedFilePath.Resolve(sourceRoot, relPath);
        _fileStorage.FilesOnDisk[fullPath.AbsolutePath] = new byte[600_000]; // 3 chunks at 256 KB

        PublicIdentityId recipientId = PublicIdentityId.New();
        DomainResult<TransferSessionId> initResult = await _handler.InitiateTransferAsync(
            channelId,
            senderId,
            [recipientId],
            sourceRoot,
            [relPath],
            DirectWanPolicy.DisabledByDefault);

        initResult.IsSuccess.Should().BeTrue();
        TransferSessionId sessionId = initResult.Value;

        // Stage offer on recipient
        InMemoryTransferSessionRepository recipientRepo = new();
        InMemoryFilterPolicyRepository policyRepo = new()
        {
            Policy = FileTransferFilterPolicy.DefaultPermissive(recipientId)
        };
        var payloadHandler = new FileTransferPayloadHandler(recipientRepo, policyRepo, _serializer, _clock, _blobStorage);
        await payloadHandler.HandleInboundAsync(new InboundPayloadContext(
            channelId,
            senderId,
            DeviceId.Primary,
            AppId.FileTransferControl,
            _payloadSender.SentPayloads[0].Payload,
            _clock.UtcNow));

        // Simulate partial resumption on disk (chunk 0 already verified on disk) and direct P2P failure -> relay fallback
        InMemoryDesktopFileStorage recipientStorage = new()
        {
            ConfiguredResumptionBitfield = Bitfield.Empty(ChunkCount.FromInt(3)).WithChunk(ChunkIndex.FromInt(0))
        };
        await using FakeTransferStreamPool recipientPool = new()
        {
            SimulateDirectConnectionFailure = true
        };
        RecordingPayloadSender recipientSender = new();
        RecordingTrackerPort recipientTracker = new();

        var recipientActionHandler = new FileTransferActionHandler(
            recipientRepo,
            recipientStorage,
            recipientSender,
            _serializer,
            recipientTracker,
            _clock,
            _blobStorage,
            _endpointDiscovery,
            recipientPool);

        DomainResult acceptResult = await recipientActionHandler.AcceptTransferAsync(
            sessionId,
            RootDirectoryPath.Parse(@"C:\Downloads"));

        acceptResult.IsSuccess.Should().BeTrue();

        // 1. Verify resumption bitfield was applied to session and transmitted in Accept payload
        TransferSession? acceptedSession = await recipientRepo.GetSessionByIdAsync(sessionId);
        acceptedSession!.LocalBitfield.HasChunk(ChunkIndex.FromInt(0)).Should().BeTrue();
        acceptedSession.LocalBitfield.HasChunk(ChunkIndex.FromInt(1)).Should().BeFalse();

        // 2. Verify Leecher announced to tracker
        recipientTracker.Announced.Should().ContainSingle(a => a.MemberId == recipientId && a.Role == SwarmPeerRole.Leecher);

        // 3. Verify croc-style direct-to-relay fallback connected to the relay pipe when direct failed
        recipientPool.ConnectedRelayRendezvous.Should().NotBeNull();
        recipientPool.ConnectedRelayRendezvous!.EndpointUri.Value.Should().Be("https://relay.percolator.net/pipe/session");
    }

    [Test]
    public async Task UT_REP_01_StaleOfferReapingServiceSweepViaVirtualClock()
    {
        PublicIdentityId sender = PublicIdentityId.New();

        TransferManifest manifest1 = TransferManifest.Create(
            TransferSessionId.New(),
            RootBundleName.Parse("OldOffer"),
            ChunkSizeBytes.Default,
            MerkleTreeBuilder.BuildFromLeaves([ChunkHash.FromBytes(new byte[32])]),
            [new FileEntry(FileIndex.FromInt(0), SanitizedRelativePath.Parse("test.bin"), FileSizeBytes.FromBytes(100), BundleByteOffset.Zero, FileLastModifiedUtc.FromDateTimeOffset(DateTimeOffset.UtcNow))]);

        TransferManifest manifest2 = TransferManifest.Create(
            TransferSessionId.New(),
            RootBundleName.Parse("RecentOffer"),
            ChunkSizeBytes.Default,
            MerkleTreeBuilder.BuildFromLeaves([ChunkHash.FromBytes(new byte[32])]),
            [new FileEntry(FileIndex.FromInt(0), SanitizedRelativePath.Parse("test.bin"), FileSizeBytes.FromBytes(100), BundleByteOffset.Zero, FileLastModifiedUtc.FromDateTimeOffset(DateTimeOffset.UtcNow))]);

        // Offer 1 created 25 hours ago
        _clock.UtcNow = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        TransferSession staleSession = TransferSession.CreateIncomingStaged(manifest1.ManifestId, sender, manifest1, SeedRatio.Zero, _clock);
        await _sessionRepo.SaveSessionAsync(staleSession);

        // Advance clock by 23 hours -> now offer 2 created 2 hours before current time
        _clock.Advance(TimeSpan.FromHours(23));
        TransferSession recentSession = TransferSession.CreateIncomingStaged(manifest2.ManifestId, sender, manifest2, SeedRatio.Zero, _clock);
        await _sessionRepo.SaveSessionAsync(recentSession);

        // Advance clock by 2 hours -> stale offer is 25 hours old, recent offer is 2 hours old
        _clock.Advance(TimeSpan.FromHours(2));

        int expired = await _reaperService.SweepAndExpireStaleOffersAsync(TimeSpan.FromHours(24));

        expired.Should().Be(1);
        staleSession.State.Should().Be(TransferSessionState.Cancelled);
        recentSession.State.Should().Be(TransferSessionState.PendingConsent);
    }

    [Test]
    public async Task UT_QRY_01_TransferSessionQueryProjectionAccuracy()
    {
        TransferSessionId sessionId = TransferSessionId.New();
        PublicIdentityId initiator = PublicIdentityId.New();
        PublicIdentityId recipient = PublicIdentityId.New();

        TransferManifest manifest = TransferManifest.Create(
            sessionId,
            RootBundleName.Parse("ProjectionTest"),
            ChunkSizeBytes.FromBytes(65536),
            MerkleTreeBuilder.BuildFromLeaves([ChunkHash.FromBytes(new byte[32]), ChunkHash.FromBytes(new byte[32])]),
            [
                new FileEntry(FileIndex.FromInt(0), SanitizedRelativePath.Parse("f0.bin"), FileSizeBytes.FromBytes(65536), BundleByteOffset.Zero, FileLastModifiedUtc.FromDateTimeOffset(DateTimeOffset.UtcNow)),
                new FileEntry(FileIndex.FromInt(1), SanitizedRelativePath.Parse("f1.bin"), FileSizeBytes.FromBytes(65536), BundleByteOffset.FromOffset(65536), FileLastModifiedUtc.FromDateTimeOffset(DateTimeOffset.UtcNow))
            ]);

        TransferSession session = TransferSession.CreateOutgoing1to1(sessionId, initiator, recipient, manifest, SeedRatio.FromMultiplier(2.0), _clock);
        session.RecordUploadedBytes(TransferByteLength.FromBytes(65536), _clock);
        await _sessionRepo.SaveSessionAsync(session);

        TransferSessionReadModel? readModel = await _queryService.GetTransferByIdAsync(sessionId);

        readModel.Should().NotBeNull();
        readModel!.SessionId.Should().Be(sessionId);
        readModel.RootName.Value.Should().Be("ProjectionTest");
        readModel.TotalBytes.Bytes.Should().Be(131072);
        readModel.UploadedBytes.Bytes.Should().Be(65536);
        readModel.CurrentSeedRatio.Multiplier.Should().Be(0.5);
        readModel.TargetSeedRatio.Multiplier.Should().Be(2.0);
        readModel.TotalChunks.Count.Should().Be(2);
        readModel.Status.Should().Be(TransferSessionState.Offered);
    }
}

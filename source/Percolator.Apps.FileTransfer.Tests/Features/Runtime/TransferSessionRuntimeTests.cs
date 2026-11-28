using System.Diagnostics;
using System.Security.Cryptography;
using Microsoft.Extensions.DependencyInjection;
using Percolator.Apps.FileTransfer.DependencyInjection;
using Percolator.Apps.FileTransfer.Domain.Common;
using Percolator.Apps.FileTransfer.Domain.ManifestManagement;
using Percolator.Apps.FileTransfer.Domain.SwarmCoordination;
using Percolator.Apps.FileTransfer.Domain.TransferNegotiation;
using Percolator.Apps.FileTransfer.Features.IngressControl;
using Percolator.Apps.FileTransfer.Features.Runtime;
using Percolator.Apps.FileTransfer.Features.TransferNegotiation;
using Percolator.Apps.FileTransfer.Ports;
using Percolator.Apps.FileTransfer.Tests.TestDoubles;
using Percolator.Domain.Channels.ValueObjects;
using Percolator.Domain.Common;
using Percolator.Domain.Identities.ValueObjects;
using Percolator.PluginSdk;
using Percolator.PluginSdk.Events;
using PublicIdentityId = Percolator.Domain.Identities.PublicIdentityId;

namespace Percolator.Apps.FileTransfer.Tests.Features.Runtime;

[TestFixture]
public sealed class TransferSessionRuntimeTests
{
    [Test]
    public async Task GroupV2_OfferBroadcastsOverRelayedGroup_AndPeerJoinAuthorizesAndPinsCerts()
    {
        ChannelId groupChannelId = ChannelId.New();
        PublicIdentityId aliceId = PublicIdentityId.New();
        PublicIdentityId bobId = PublicIdentityId.New();
        PublicIdentityId charlieId = PublicIdentityId.New();

        FakeDateTimeProvider clock = new();
        TestPayloadSerializer serializer = new();
        InMemoryBlobStorageService blobStorage = new();
        FakeNetworkEndpointDiscoveryPort endpointDiscovery = new();

        await using var alice = CreateNode(aliceId, clock, serializer, blobStorage, endpointDiscovery);
        await using var bob = CreateNode(bobId, clock, serializer, blobStorage, endpointDiscovery);
        await using var charlie = CreateNode(charlieId, clock, serializer, blobStorage, endpointDiscovery);

        RootDirectoryPath aliceRoot = RootDirectoryPath.Parse(@"C:\AliceShare");
        SanitizedRelativePath fileRel = SanitizedRelativePath.Parse("deck.bin");
        byte[] fileData = RandomNumberGenerator.GetBytes(300_000); // 2 chunks at 256 KB
        alice.FileStorage.FilesOnDisk[ResolvedFilePath.Resolve(aliceRoot, fileRel).AbsolutePath] = fileData;

        // 1. Alice initiates Group V2 transfer to [Bob, Charlie]
        DomainResult<TransferSessionId> initResult = await alice.ActionHandler.InitiateTransferAsync(
            groupChannelId,
            aliceId,
            [bobId, charlieId],
            aliceRoot,
            [fileRel],
            DirectWanPolicy.DisabledByDefault);

        initResult.IsSuccess.Should().BeTrue();
        TransferSessionId sessionId = initResult.Value;

        // Verify SINGLE Group V2 SenderKey broadcast (RecipientIdentityId == null, Route == RelayedGroup)
        alice.PayloadSender.SentPayloads.Should().ContainSingle();
        OutboundPayloadContext offerOutbound = alice.PayloadSender.SentPayloads[0];
        offerOutbound.ChannelId.Should().Be(groupChannelId);
        offerOutbound.RecipientIdentityId.Should().BeNull();
        offerOutbound.Route.Should().Be(DeliveryRoute.RelayedGroup);
        offerOutbound.Payload.Span[0].Should().Be((byte)FileTransferControlOpcode.Offer);

        FileTransferOfferPayload parsedOffer = serializer.Deserialize<FileTransferOfferPayload>(offerOutbound.Payload.Slice(1)).Value;
        parsedOffer.IsGroupTransfer.Should().BeTrue();
        using var aliceCert = await alice.CertStore.GetAsync(sessionId);
        parsedOffer.SenderCertSha256.Should().Be(aliceCert!.Fingerprint);

        // 2. Deliver Group V2 Offer to Bob and Charlie
        (await bob.PayloadHandler.HandleInboundAsync(new InboundPayloadContext(
            groupChannelId, bobId, aliceId, DeviceId.Primary, AppId.FileTransferControl, offerOutbound.Payload, clock.UtcNow)))
            .IsSuccess.Should().BeTrue();

        (await charlie.PayloadHandler.HandleInboundAsync(new InboundPayloadContext(
            groupChannelId, charlieId, aliceId, DeviceId.Primary, AppId.FileTransferControl, offerOutbound.Payload, clock.UtcNow)))
            .IsSuccess.Should().BeTrue();

        // 3. Bob accepts -> broadcasts Group V2 PeerJoin over SenderKey channel
        (await bob.ActionHandler.AcceptTransferAsync(sessionId, RootDirectoryPath.Parse(@"C:\BobDownloads")))
            .IsSuccess.Should().BeTrue();

        bob.PayloadSender.SentPayloads.Should().ContainSingle();
        OutboundPayloadContext bobPeerJoin = bob.PayloadSender.SentPayloads[0];
        bobPeerJoin.Payload.Span[0].Should().Be((byte)FileTransferControlOpcode.PeerJoin);
        bobPeerJoin.RecipientIdentityId.Should().BeNull();
        bobPeerJoin.Route.Should().Be(DeliveryRoute.RelayedGroup);

        using var bobCert = await bob.CertStore.GetAsync(sessionId);

        // 4. Charlie accepts and processes Bob's PeerJoin -> authorizes Bob and pins Bob's cert
        (await charlie.ActionHandler.AcceptTransferAsync(sessionId, RootDirectoryPath.Parse(@"C:\CharlieDownloads")))
            .IsSuccess.Should().BeTrue();

        (await charlie.PayloadHandler.HandleInboundAsync(new InboundPayloadContext(
            groupChannelId, charlieId, bobId, DeviceId.Primary, AppId.FileTransferControl, bobPeerJoin.Payload, clock.UtcNow)))
            .IsSuccess.Should().BeTrue();

        TransferSession? charlieSession = await charlie.SessionRepo.GetSessionByIdAsync(sessionId);
        charlieSession!.Topology.ContainsParticipant(bobId).Should().BeTrue();
        charlie.Runtime.GetPinnedPeerCertificate(sessionId, bobId).Should().Be(bobCert!.Fingerprint);
    }

    [Test]
    public async Task GroupV2_MemberLeftEvent_ImmediatelyRevokesCertAndDisposesChannel()
    {
        ChannelId groupChannelId = ChannelId.New();
        PublicIdentityId aliceId = PublicIdentityId.New();
        PublicIdentityId bobId = PublicIdentityId.New();
        PublicIdentityId charlieId = PublicIdentityId.New();

        FakeDateTimeProvider clock = new();
        TestPayloadSerializer serializer = new();
        InMemoryBlobStorageService blobStorage = new();
        FakeNetworkEndpointDiscoveryPort endpointDiscovery = new();

        await using var alice = CreateNode(aliceId, clock, serializer, blobStorage, endpointDiscovery);
        await using var bob = CreateNode(bobId, clock, serializer, blobStorage, endpointDiscovery);
        await using var charlie = CreateNode(charlieId, clock, serializer, blobStorage, endpointDiscovery);

        RootDirectoryPath aliceRoot = RootDirectoryPath.Parse(@"C:\AliceEvict");
        SanitizedRelativePath fileRel = SanitizedRelativePath.Parse("secret.bin");
        alice.FileStorage.FilesOnDisk[ResolvedFilePath.Resolve(aliceRoot, fileRel).AbsolutePath] = RandomNumberGenerator.GetBytes(128_000);

        TransferSessionId sessionId = (await alice.ActionHandler.InitiateTransferAsync(
            groupChannelId, aliceId, [bobId, charlieId], aliceRoot, [fileRel], DirectWanPolicy.DisabledByDefault)).Value;

        var offerBytes = alice.PayloadSender.SentPayloads[0].Payload;
        await bob.PayloadHandler.HandleInboundAsync(new InboundPayloadContext(
            groupChannelId, bobId, aliceId, DeviceId.Primary, AppId.FileTransferControl, offerBytes, clock.UtcNow));
        await charlie.PayloadHandler.HandleInboundAsync(new InboundPayloadContext(
            groupChannelId, charlieId, aliceId, DeviceId.Primary, AppId.FileTransferControl, offerBytes, clock.UtcNow));

        await bob.ActionHandler.AcceptTransferAsync(sessionId, RootDirectoryPath.Parse(@"C:\BobEvict"));
        await charlie.ActionHandler.AcceptTransferAsync(sessionId, RootDirectoryPath.Parse(@"C:\CharlieEvict"));

        var charliePeerJoin = charlie.PayloadSender.SentPayloads.Single(p => p.Payload.Span[0] == (byte)FileTransferControlOpcode.PeerJoin);
        await bob.PayloadHandler.HandleInboundAsync(new InboundPayloadContext(
            groupChannelId, bobId, charlieId, DeviceId.Primary, AppId.FileTransferControl, charliePeerJoin.Payload, clock.UtcNow));

        using var bobCert = await bob.CertStore.GetAsync(sessionId);
        using var charlieCert = await charlie.CertStore.GetAsync(sessionId);
        var (bobSide, charlieSide) = InMemoryPairedTransferDataChannel.CreatePair(bobCert!.Fingerprint, charlieCert!.Fingerprint);

        (await bob.Runtime.AttachAuthenticatedChannelAsync(sessionId, charlieId, bobSide)).IsSuccess.Should().BeTrue();
        bob.Runtime.GetPinnedPeerCertificate(sessionId, charlieId).Should().Be(charlieCert.Fingerprint);
        bob.Runtime.GetConnectedPeers(sessionId).Should().Contain(charlieId);

        // Publish Group V2 MemberLeft eviction event (Epoch E -> E+1) on Bob's event bus
        await bob.EventBus.PublishAsync(new GroupMemberRemovedApplicationEvent(
            groupChannelId,
            charlieId,
            new EpochNumber(2),
            clock.UtcNow));

        // Evicted peer's cert is immediately purged, topology updated, and live channel disposed
        bob.Runtime.GetPinnedPeerCertificate(sessionId, charlieId).Should().BeNull();
        bob.Runtime.GetConnectedPeers(sessionId).Should().NotContain(charlieId);
        bob.Runtime.GetActiveSession(sessionId)!.Topology.ContainsParticipant(charlieId).Should().BeFalse();
        bobSide.IsDisposed.Should().BeTrue();

        // Subsequent inbound ICE signal from evicted Charlie MUST be rejected
        var sneakySignal = new FileTransferIceSignalPayload(sessionId, IceSignalKind.SdpOffer, "v=0", null, charlieCert.Fingerprint);
        DomainResult signalResult = await bob.Runtime.HandleInboundIceSignalAsync(
            sessionId,
            charlieId,
            sneakySignal);

        signalResult.IsFailure.Should().BeTrue();
        signalResult.Error.Code.Should().Be("Transfer.UnauthorizedOrRevokedPeer");

        await charlieSide.DisposeAsync();
    }

    [Test]
    public async Task OfflineAuthorSeeding_CompletedDownloadKeepsSeedingAndServesLateJoinerAfterAuthorDisconnects()
    {
        ChannelId groupChannelId = ChannelId.New();
        PublicIdentityId aliceId = PublicIdentityId.New();
        PublicIdentityId bobId = PublicIdentityId.New();
        PublicIdentityId charlieId = PublicIdentityId.New();

        FakeDateTimeProvider clock = new();
        TestPayloadSerializer serializer = new();
        InMemoryBlobStorageService blobStorage = new();
        FakeNetworkEndpointDiscoveryPort endpointDiscovery = new();

        var alice = CreateNode(aliceId, clock, serializer, blobStorage, endpointDiscovery);
        await using var bob = CreateNode(bobId, clock, serializer, blobStorage, endpointDiscovery);
        await using var charlie = CreateNode(charlieId, clock, serializer, blobStorage, endpointDiscovery);

        RootDirectoryPath aliceRoot = RootDirectoryPath.Parse(@"C:\AliceSeed");
        RootDirectoryPath bobRoot = RootDirectoryPath.Parse(@"C:\BobSeed");
        RootDirectoryPath charlieRoot = RootDirectoryPath.Parse(@"C:\CharlieSeed");
        SanitizedRelativePath fileRel = SanitizedRelativePath.Parse("bundle.dat");

        byte[] originalBytes = RandomNumberGenerator.GetBytes(600_000); // 3 chunks at 256 KB
        alice.FileStorage.FilesOnDisk[ResolvedFilePath.Resolve(aliceRoot, fileRel).AbsolutePath] = originalBytes;

        // 1. Alice initiates Group V2 transfer to [Bob, Charlie]
        TransferSessionId sessionId = (await alice.ActionHandler.InitiateTransferAsync(
            groupChannelId, aliceId, [bobId, charlieId], aliceRoot, [fileRel], DirectWanPolicy.DisabledByDefault)).Value;

        var offerBytes = alice.PayloadSender.SentPayloads[0].Payload;

        // Deliver offer to Bob and Charlie
        await bob.PayloadHandler.HandleInboundAsync(new InboundPayloadContext(
            groupChannelId, bobId, aliceId, DeviceId.Primary, AppId.FileTransferControl, offerBytes, clock.UtcNow));
        await charlie.PayloadHandler.HandleInboundAsync(new InboundPayloadContext(
            groupChannelId, charlieId, aliceId, DeviceId.Primary, AppId.FileTransferControl, offerBytes, clock.UtcNow));

        // 2. Bob accepts and downloads all 3 chunks directly from Alice
        await bob.ActionHandler.AcceptTransferAsync(sessionId, bobRoot);
        var bobPeerJoin = bob.PayloadSender.SentPayloads.Single(p => p.Payload.Span[0] == (byte)FileTransferControlOpcode.PeerJoin);

        // Pin Bob on Alice
        using var aliceCert = await alice.CertStore.GetAsync(sessionId);
        using var bobCert = await bob.CertStore.GetAsync(sessionId);
        await alice.Runtime.RegisterPinnedPeerCertificateAsync(sessionId, bobId, bobCert!.Fingerprint);

        var (aliceToBob, bobToAlice) = InMemoryPairedTransferDataChannel.CreatePair(aliceCert!.Fingerprint, bobCert.Fingerprint);
        (await alice.Runtime.AttachAuthenticatedChannelAsync(sessionId, bobId, aliceToBob)).IsSuccess.Should().BeTrue();
        (await bob.Runtime.AttachAuthenticatedChannelAsync(sessionId, aliceId, bobToAlice)).IsSuccess.Should().BeTrue();

        // Wait deterministically for Bob to verify all 3 chunks and enter Seeding state
        await WaitUntilAsync(() => bob.Runtime.GetActiveSession(sessionId)?.State == TransferSessionState.Seeding, TimeSpan.FromSeconds(5));

        bob.Runtime.GetActiveSession(sessionId)!.IsDownloadComplete.Should().BeTrue();
        bob.Runtime.GetActiveSession(sessionId)!.State.Should().Be(TransferSessionState.Seeding);
        bob.FileStorage.FilesOnDisk[ResolvedFilePath.Resolve(bobRoot, fileRel).AbsolutePath].Should().Equal(originalBytes);

        // 3. Original author Alice goes COMPLETELY OFFLINE (vault lock / disconnect)
        await alice.DisposeAsync();
        alice.Runtime.ActiveSessionCount.Should().Be(0);

        // 4. Late-joining Charlie accepts AFTER Alice is offline, exchanges PeerJoin with Bob, and downloads from Bob!
        await charlie.ActionHandler.AcceptTransferAsync(sessionId, charlieRoot);
        var charliePeerJoin = charlie.PayloadSender.SentPayloads.Single(p => p.Payload.Span[0] == (byte)FileTransferControlOpcode.PeerJoin);

        // Deliver Bob's PeerJoin to Charlie and Charlie's PeerJoin to Bob
        await charlie.PayloadHandler.HandleInboundAsync(new InboundPayloadContext(
            groupChannelId, charlieId, bobId, DeviceId.Primary, AppId.FileTransferControl, bobPeerJoin.Payload, clock.UtcNow));
        await bob.PayloadHandler.HandleInboundAsync(new InboundPayloadContext(
            groupChannelId, bobId, charlieId, DeviceId.Primary, AppId.FileTransferControl, charliePeerJoin.Payload, clock.UtcNow));

        using var charlieCert = await charlie.CertStore.GetAsync(sessionId);
        var (bobToCharlie, charlieToBob) = InMemoryPairedTransferDataChannel.CreatePair(bobCert.Fingerprint, charlieCert!.Fingerprint);

        (await bob.Runtime.AttachAuthenticatedChannelAsync(sessionId, charlieId, bobToCharlie)).IsSuccess.Should().BeTrue();
        (await charlie.Runtime.AttachAuthenticatedChannelAsync(sessionId, bobId, charlieToBob)).IsSuccess.Should().BeTrue();

        // Wait deterministically for Charlie to complete download exclusively from Bob (while Alice is offline!)
        await WaitUntilAsync(() => charlie.Runtime.GetActiveSession(sessionId)?.State == TransferSessionState.Seeding, TimeSpan.FromSeconds(5));

        charlie.Runtime.GetActiveSession(sessionId)!.IsDownloadComplete.Should().BeTrue();
        charlie.FileStorage.FilesOnDisk[ResolvedFilePath.Resolve(charlieRoot, fileRel).AbsolutePath].Should().Equal(originalBytes);

        // 5. Bob manually stops seeding -> transitions to Completed, unregisters from tracker, deletes session cert
        await bob.SessionRepo.SaveSessionAsync(bob.Runtime.GetActiveSession(sessionId)!);
        DomainResult stopResult = await bob.ActionHandler.StopSeedingAsync(sessionId);
        stopResult.IsSuccess.Should().BeTrue();
        (await bob.SessionRepo.GetSessionByIdAsync(sessionId))!.State.Should().Be(TransferSessionState.Completed);
        bob.Runtime.HasActiveSession(sessionId).Should().BeFalse();
        bob.TrackerPort.Unregistered.Should().NotBeEmpty();
        bob.CertStore.DeletedSessions.Should().Contain(sessionId);
    }

    [Test]
    public async Task SeedingTtlExpiry_KeepsSeedingUntilTtlThenStopsAndUnregisters()
    {
        FakeDateTimeProvider clock = new();
        TestPayloadSerializer serializer = new();
        InMemoryBlobStorageService blobStorage = new();
        FakeNetworkEndpointDiscoveryPort endpointDiscovery = new();
        PublicIdentityId bobId = PublicIdentityId.New();
        PublicIdentityId aliceId = PublicIdentityId.New();

        await using var bob = CreateNode(bobId, clock, serializer, blobStorage, endpointDiscovery, seedingTtl: TimeSpan.FromHours(24));

        TransferSessionId sessionId = TransferSessionId.New();
        byte[] chunkBytes = RandomNumberGenerator.GetBytes(1024);
        TransferManifest manifest = TransferManifest.Create(
            sessionId,
            RootBundleName.Parse("TtlTest"),
            ChunkSizeBytes.Default,
            MerkleTreeBuilder.BuildFromLeaves([MerkleTreeBuilder.HashLeaf(chunkBytes)]),
            [new FileEntry(FileIndex.FromInt(0), SanitizedRelativePath.Parse("a.bin"), FileSizeBytes.FromBytes(1024), BundleByteOffset.Zero, FileLastModifiedUtc.FromDateTimeOffset(clock.UtcNow))]);

        TransferSession session = TransferSession.CreateIncomingStaged(sessionId, aliceId, manifest, SeedRatio.Unlimited, clock);
        session.AcceptTransfer(SelectedFileIndices.All(manifest.TotalFiles), clock, Bitfield.Full(manifest.TotalChunks));
        session.State.Should().Be(TransferSessionState.Seeding);

        SwarmRendezvousId rendezvousId = SwarmRendezvousId.Generate();
        EphemeralSwarmPeerId ephemeralPeerId = EphemeralSwarmPeerId.Generate();
        var ctx = new TransferSessionTransportContext(
            sessionId, ChannelId.New(), bobId, rendezvousId, ephemeralPeerId, StreamAuthToken.Generate(),
            DirectWanPolicy.DisabledByDefault, null, [], RootDirectoryPath.Parse(@"C:\BobTtl"));

        await bob.Runtime.StartOrUpdateSessionAsync(session, ctx, RootDirectoryPath.Parse(@"C:\BobTtl"));

        // At +23h: MUST remain Seeding while unlocked
        clock.Advance(TimeSpan.FromHours(23));
        (await bob.Runtime.EvaluateSeedingRetentionAsync()).Should().Be(0);
        bob.Runtime.HasActiveSession(sessionId).Should().BeTrue();
        session.State.Should().Be(TransferSessionState.Seeding);

        // At +24h: TTL expires -> stops seeding & unregisters from tracker
        clock.Advance(TimeSpan.FromHours(1));
        (await bob.Runtime.EvaluateSeedingRetentionAsync()).Should().Be(1);
        bob.Runtime.HasActiveSession(sessionId).Should().BeFalse();
        session.State.Should().Be(TransferSessionState.Completed);
        bob.TrackerPort.Unregistered.Should().ContainSingle(u => u.Rendezvous == rendezvousId && u.EphemeralPeerId == ephemeralPeerId);
    }

    [Test]
    public async Task VaultLock_DisposeAsync_TearsDownAllSocketsAndUnregistersWithinOneSecond()
    {
        FakeDateTimeProvider clock = new();
        TestPayloadSerializer serializer = new();
        InMemoryBlobStorageService blobStorage = new();
        FakeNetworkEndpointDiscoveryPort endpointDiscovery = new();
        PublicIdentityId localId = PublicIdentityId.New();
        PublicIdentityId peerId = PublicIdentityId.New();

        var node = CreateNode(localId, clock, serializer, blobStorage, endpointDiscovery);
        node.EventBus.SubscriptionCount.Should().BeGreaterThan(0, "Subscribes to active domain and transport application events");

        List<InMemoryPairedTransferDataChannel> activeChannels = [];
        List<SwarmRendezvousId> rendezvousIds = [];

        for (int i = 0; i < 3; i++)
        {
            TransferSessionId sessionId = TransferSessionId.New();
            TransferManifest manifest = TransferManifest.Create(
                sessionId,
                RootBundleName.Parse($"Session{i}"),
                ChunkSizeBytes.Default,
                MerkleTreeBuilder.BuildFromLeaves([ChunkHash.FromBytes(new byte[32])]),
                [new FileEntry(FileIndex.FromInt(0), SanitizedRelativePath.Parse("f.bin"), FileSizeBytes.FromBytes(1024), BundleByteOffset.Zero, FileLastModifiedUtc.FromDateTimeOffset(clock.UtcNow))]);

            TransferSession session = TransferSession.CreateOutgoing1to1(sessionId, localId, peerId, manifest, SeedRatio.Unlimited, clock);
            SwarmRendezvousId rId = SwarmRendezvousId.Generate();
            rendezvousIds.Add(rId);

            var ctx = new TransferSessionTransportContext(
                sessionId, ChannelId.New(), localId, rId, EphemeralSwarmPeerId.Generate(), StreamAuthToken.Generate(),
                DirectWanPolicy.DisabledByDefault, null, [], RootDirectoryPath.Parse(@"C:\Seed"));

            await node.Runtime.StartOrUpdateSessionAsync(session, ctx, RootDirectoryPath.Parse(@"C:\Seed"));

            CertSha256Fingerprint localFp = CertSha256Fingerprint.FromBytes(RandomNumberGenerator.GetBytes(32));
            CertSha256Fingerprint peerFp = CertSha256Fingerprint.FromBytes(RandomNumberGenerator.GetBytes(32));
            await node.Runtime.RegisterPinnedPeerCertificateAsync(sessionId, peerId, peerFp);

            var (sideA, sideB) = InMemoryPairedTransferDataChannel.CreatePair(localFp, peerFp);
            (await node.Runtime.AttachAuthenticatedChannelAsync(sessionId, peerId, sideA)).IsSuccess.Should().BeTrue();
            activeChannels.Add(sideA);
            activeChannels.Add(sideB);
        }

        node.Runtime.ActiveSessionCount.Should().Be(3);

        // Vault lock teardown: DisposeAsync must finish well under 1s, close all sockets, unregister all sessions, and unsubscribe event bus
        Stopwatch sw = Stopwatch.StartNew();
        await node.Runtime.DisposeAsync();
        sw.Stop();

        sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(1));
        node.Runtime.ActiveSessionCount.Should().Be(0);
        node.PeerTransport.CloseAllCallCount.Should().Be(1);
        node.TrackerPort.Unregistered.Should().HaveCount(rendezvousIds.Count);
        foreach (SwarmRendezvousId rendezvous in rendezvousIds)
        {
            node.TrackerPort.Unregistered.Select(u => u.Rendezvous).Should().Contain(rendezvous);
        }
        node.EventBus.SubscriptionCount.Should().Be(0);

        for (int i = 0; i < activeChannels.Count; i += 2)
        {
            activeChannels[i].IsDisposed.Should().BeTrue();
            await activeChannels[i + 1].DisposeAsync();
        }
    }

    [Test]
    public async Task AttachAuthenticatedChannel_RejectsMismatchedCertFingerprint()
    {
        FakeDateTimeProvider clock = new();
        TestPayloadSerializer serializer = new();
        InMemoryBlobStorageService blobStorage = new();
        FakeNetworkEndpointDiscoveryPort endpointDiscovery = new();
        PublicIdentityId localId = PublicIdentityId.New();
        PublicIdentityId peerId = PublicIdentityId.New();

        await using var node = CreateNode(localId, clock, serializer, blobStorage, endpointDiscovery);

        TransferSessionId sessionId = TransferSessionId.New();
        TransferManifest manifest = TransferManifest.Create(
            sessionId,
            RootBundleName.Parse("PinTest"),
            ChunkSizeBytes.Default,
            MerkleTreeBuilder.BuildFromLeaves([ChunkHash.FromBytes(new byte[32])]),
            [new FileEntry(FileIndex.FromInt(0), SanitizedRelativePath.Parse("f.bin"), FileSizeBytes.FromBytes(1024), BundleByteOffset.Zero, FileLastModifiedUtc.FromDateTimeOffset(clock.UtcNow))]);

        TransferSession session = TransferSession.CreateOutgoing1to1(sessionId, localId, peerId, manifest, SeedRatio.Unlimited, clock);
        var ctx = new TransferSessionTransportContext(
            sessionId, ChannelId.New(), localId, SwarmRendezvousId.Generate(), EphemeralSwarmPeerId.Generate(), StreamAuthToken.Generate(),
            DirectWanPolicy.DisabledByDefault, null, [], RootDirectoryPath.Parse(@"C:\Pin"));

        await node.Runtime.StartOrUpdateSessionAsync(session, ctx, RootDirectoryPath.Parse(@"C:\Pin"));

        CertSha256Fingerprint pinnedPeerFp = CertSha256Fingerprint.FromBytes(RandomNumberGenerator.GetBytes(32));
        CertSha256Fingerprint attackerFp = CertSha256Fingerprint.FromBytes(RandomNumberGenerator.GetBytes(32));
        await node.Runtime.RegisterPinnedPeerCertificateAsync(sessionId, peerId, pinnedPeerFp);

        var (localSide, attackerSide) = InMemoryPairedTransferDataChannel.CreatePair(
            CertSha256Fingerprint.FromBytes(RandomNumberGenerator.GetBytes(32)),
            attackerFp);

        DomainResult result = await node.Runtime.AttachAuthenticatedChannelAsync(sessionId, peerId, localSide);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be(TransferTransportErrorCodes.DtlsFingerprintMismatch);
        localSide.IsDisposed.Should().BeTrue();
        await attackerSide.DisposeAsync();
    }

    [Test]
    public async Task AddFileTransferApp_RemovesNullTransferStreamPool_AndPassesValidateScopes()
    {
        ServiceCollection services = new();
        services.AddFileTransferApp();

        // Verify NullTransferStreamPool / ITransferStreamPool is NOT registered in production DI
        services.Any(d => d.ServiceType == typeof(ITransferStreamPool)).Should().BeFalse();

        // Supply Infrastructure port stubs with matching production lifetimes (Singleton / Scoped)
        services.AddSingleton<IDateTimeProvider>(new FakeDateTimeProvider());
        services.AddSingleton<IPayloadSerializer>(new TestPayloadSerializer());
        services.AddSingleton<IBlobStorageService>(new InMemoryBlobStorageService());
        services.AddSingleton<IDesktopFileStorage>(new InMemoryDesktopFileStorage());
        services.AddSingleton<ITransferRateLimiter>(new RecordingTransferRateLimiter());
        services.AddSingleton<ITransferPeerTransport>(new FakeTransferPeerTransport());
        services.AddSingleton<ITransferSessionCertificateStore>(new InMemoryTransferSessionCertificateStore());
        services.AddSingleton<IDistributedTrackerPort>(new RecordingTrackerPort());
        services.AddSingleton<INetworkEndpointDiscoveryPort>(new FakeNetworkEndpointDiscoveryPort());
        services.AddSingleton<IEventSubscriber>(new InMemoryEventBusForTests());
        services.AddSingleton<IFileTransferFilterPolicyRepository>(new InMemoryFilterPolicyRepository());
        services.AddScoped<IPayloadSender, RecordingPayloadSender>();
        services.AddScoped<ITransferSessionRepository, InMemoryTransferSessionRepository>();
        services.AddSingleton<ITransferSessionTransportStore, InMemoryTransferSessionTransportStore>();

        await using ServiceProvider provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateScopes = true,
            ValidateOnBuild = true
        });

        ITransferSessionRuntime runtime = provider.GetRequiredService<ITransferSessionRuntime>();
        runtime.Should().BeOfType<TransferSessionRuntime>();

        using IServiceScope scope = provider.CreateScope();
        IFileTransferActionHandler actionHandler = scope.ServiceProvider.GetRequiredService<IFileTransferActionHandler>();
        IAppPayloadHandler payloadHandler = scope.ServiceProvider.GetRequiredService<IAppPayloadHandler>();

        actionHandler.Should().BeOfType<FileTransferActionHandler>();
        payloadHandler.Should().BeOfType<FileTransferPayloadHandler>();
    }

    [Test]
    public async Task EventBusTrickleIceCandidates_DispatchesOutboundPayloadContextCorrectly()
    {
        var clock = new FakeDateTimeProvider();
        var serializer = new TestPayloadSerializer();
        var blobStorage = new InMemoryBlobStorageService();
        var endpointDiscovery = new FakeNetworkEndpointDiscoveryPort();

        var aliceId = PublicIdentityId.New();
        var bobId = PublicIdentityId.New();
        var channelId = ChannelId.New();
        var sessionId = TransferSessionId.New();

        await using var alice = CreateNode(aliceId, clock, serializer, blobStorage, endpointDiscovery);

        var manifest = TransferManifest.Create(
            sessionId,
            RootBundleName.Parse("Bundle"),
            ChunkSizeBytes.Default,
            MerkleTreeBuilder.BuildFromLeaves([ChunkHash.FromBytes(new byte[32])]),
            [new FileEntry(FileIndex.FromInt(0), SanitizedRelativePath.Parse("f0.dat"), FileSizeBytes.FromBytes(100), BundleByteOffset.Zero, FileLastModifiedUtc.FromDateTimeOffset(clock.UtcNow))]);

        // 1. OneToOne Session Setup
        var session1to1 = TransferSession.CreateOutgoing1to1(sessionId, aliceId, bobId, manifest, SeedRatio.Zero, clock);
        var transport1to1 = new TransferSessionTransportContext(
            sessionId,
            channelId,
            aliceId,
            SwarmRendezvousId.Generate(),
            EphemeralSwarmPeerId.Generate(),
            StreamAuthToken.Generate(),
            DirectWanPolicy.DisabledByDefault,
            RelayRendezvous: null,
            DirectCandidates: [],
            LocalRootPath: RootDirectoryPath.Parse(@"C:\Alice"),
            IsGroupTransfer: false);

        CertSha256Fingerprint bobCertFp = CertSha256Fingerprint.FromBytes(RandomNumberGenerator.GetBytes(32));

        await alice.Runtime.StartOrUpdateSessionAsync(session1to1, transport1to1, RootDirectoryPath.Parse(@"C:\Alice"));
        await alice.Runtime.RegisterPinnedPeerCertificateAsync(sessionId, bobId, bobCertFp);

        await alice.Runtime.InitiateDirectConnectionAsync(sessionId, bobId);
        var fakeConn = alice.PeerTransport.CreatedConnections.Single(c => c.SessionId == sessionId);

        alice.PayloadSender.SentPayloads.Clear();

        var candidateEvent = new TransferIceCandidateDiscoveredEvent(
            Guid.NewGuid(),
            clock.UtcNow,
            sessionId,
            bobId,
            "candidate:1 1 UDP 2122260223 192.168.1.100 50000 typ host",
            "0",
            fakeConn.LocalDtlsFingerprint);

        await alice.EventBus.PublishAsync(candidateEvent);

        alice.PayloadSender.SentPayloads.Should().ContainSingle();
        var sent1to1 = alice.PayloadSender.SentPayloads[0];
        sent1to1.Route.Should().Be(DeliveryRoute.RelayedOneToOne);
        sent1to1.RecipientIdentityId.Should().Be(bobId);
        sent1to1.ChannelId.Should().Be(channelId);
        sent1to1.Payload.Span[0].Should().Be((byte)FileTransferControlOpcode.IceSignal);

        var signalPayload = serializer.Deserialize<FileTransferIceSignalPayload>(sent1to1.Payload.Slice(1)).Value;
        signalPayload.Kind.Should().Be(IceSignalKind.IceCandidate);
        signalPayload.SdpOrCandidate.Should().Be(candidateEvent.Candidate);
        signalPayload.TargetPeerId.Should().BeNull("1:1 transfer should not populate TargetPeerId");

        // 2. Stop session and verify candidate is ignored
        alice.PayloadSender.SentPayloads.Clear();
        await alice.Runtime.StopSessionAsync(sessionId);

        await alice.EventBus.PublishAsync(candidateEvent);
        alice.PayloadSender.SentPayloads.Should().BeEmpty("Candidate published after session stop must be ignored");
    }

    [Test]
    public async Task EventBusTrickleIceCandidates_GroupTransfer_PopulatesTargetPeerIdAndRelayedGroupRoute()
    {
        FakeDateTimeProvider clock = new();
        TestPayloadSerializer serializer = new();
        InMemoryBlobStorageService blobStorage = new();
        FakeNetworkEndpointDiscoveryPort endpointDiscovery = new();

        var aliceId = PublicIdentityId.New();
        var bobId = PublicIdentityId.New();
        var channelId = ChannelId.New();
        var sessionId = TransferSessionId.New();

        await using var alice = CreateNode(aliceId, clock, serializer, blobStorage, endpointDiscovery);

        var manifest = TransferManifest.Create(
            sessionId,
            RootBundleName.Parse("GroupBundle"),
            ChunkSizeBytes.Default,
            MerkleTreeBuilder.BuildFromLeaves([ChunkHash.FromBytes(new byte[32])]),
            [new FileEntry(FileIndex.FromInt(0), SanitizedRelativePath.Parse("f0.dat"), FileSizeBytes.FromBytes(100), BundleByteOffset.Zero, FileLastModifiedUtc.FromDateTimeOffset(clock.UtcNow))]);

        var groupSession = TransferSession.CreateOutgoingMultiPeer(sessionId, aliceId, [bobId], manifest, SeedRatio.Zero, clock);
        var groupTransport = new TransferSessionTransportContext(
            sessionId,
            channelId,
            aliceId,
            SwarmRendezvousId.Generate(),
            EphemeralSwarmPeerId.Generate(),
            StreamAuthToken.Generate(),
            DirectWanPolicy.DisabledByDefault,
            RelayRendezvous: null,
            DirectCandidates: [],
            LocalRootPath: RootDirectoryPath.Parse(@"C:\Alice"),
            IsGroupTransfer: true);

        CertSha256Fingerprint bobCertFp = CertSha256Fingerprint.FromBytes(RandomNumberGenerator.GetBytes(32));

        await alice.Runtime.StartOrUpdateSessionAsync(groupSession, groupTransport, RootDirectoryPath.Parse(@"C:\Alice"));
        await alice.Runtime.RegisterPinnedPeerCertificateAsync(sessionId, bobId, bobCertFp);

        await alice.Runtime.InitiateDirectConnectionAsync(sessionId, bobId);
        var fakeConn = alice.PeerTransport.CreatedConnections.Single(c => c.SessionId == sessionId);

        alice.PayloadSender.SentPayloads.Clear();

        var candidateEvent = new TransferIceCandidateDiscoveredEvent(
            Guid.NewGuid(),
            clock.UtcNow,
            sessionId,
            bobId,
            "candidate:2 1 UDP 2122260223 192.168.1.101 50001 typ host",
            "0",
            fakeConn.LocalDtlsFingerprint);

        await alice.EventBus.PublishAsync(candidateEvent);

        alice.PayloadSender.SentPayloads.Should().ContainSingle();
        var sentGroup = alice.PayloadSender.SentPayloads[0];
        sentGroup.Route.Should().Be(DeliveryRoute.RelayedGroup);
        sentGroup.RecipientIdentityId.Should().BeNull("Group V2 broadcast must omit RecipientIdentityId");
        sentGroup.ChannelId.Should().Be(channelId);
        sentGroup.Payload.Span[0].Should().Be((byte)FileTransferControlOpcode.IceSignal);

        var signalPayload = serializer.Deserialize<FileTransferIceSignalPayload>(sentGroup.Payload.Slice(1)).Value;
        signalPayload.Kind.Should().Be(IceSignalKind.IceCandidate);
        signalPayload.SdpOrCandidate.Should().Be(candidateEvent.Candidate);
        signalPayload.TargetPeerId.Should().Be(bobId, "Group transfer ICE candidate must address the specific target peer");
    }

    private static TestTransferNode CreateNode(
        PublicIdentityId localId,
        FakeDateTimeProvider clock,
        IPayloadSerializer serializer,
        IBlobStorageService blobStorage,
        INetworkEndpointDiscoveryPort endpointDiscovery,
        TimeSpan? seedingTtl = null)
    {
        InMemoryTransferSessionRepository sessionRepo = new();
        InMemoryTransferSessionTransportStore transportStore = new();
        InMemoryDesktopFileStorage fileStorage = new();
        RecordingPayloadSender payloadSender = new();
        RecordingTrackerPort trackerPort = new();
        InMemoryFilterPolicyRepository policyRepo = new() { Policy = FileTransferFilterPolicy.DefaultPermissive(localId) };
        InMemoryTransferSessionCertificateStore certStore = new();
        FakeTransferPeerTransport peerTransport = new();
        InMemoryEventBusForTests eventBus = new();
        FileTransferIngressFilter filter = new(policyRepo) { IsEnabled = true };

        TransferSessionRuntime runtime = new(
            peerTransport,
            certStore,
            trackerPort,
            fileStorage,
            new RecordingTransferRateLimiter(),
            clock,
            eventBus,
            serializer,
            new DirectBackgroundPayloadDispatcher(payloadSender),
            new TransferSessionRuntimeOptions(SeedingTtl: seedingTtl));

        FileTransferActionHandler actionHandler = new(
            sessionRepo,
            fileStorage,
            payloadSender,
            serializer,
            trackerPort,
            clock,
            filter,
            blobStorage,
            endpointDiscovery,
            runtime,
            certStore,
            transportStore);

        FileTransferPayloadHandler payloadHandler = new(
            sessionRepo,
            policyRepo,
            serializer,
            clock,
            blobStorage,
            runtime,
            new DirectBackgroundPayloadDispatcher(payloadSender),
            certStore,
            transportStore);

        return new TestTransferNode(
            sessionRepo,
            fileStorage,
            payloadSender,
            trackerPort,
            certStore,
            peerTransport,
            eventBus,
            runtime,
            actionHandler,
            payloadHandler);
    }

    private static async Task WaitUntilAsync(Func<bool> predicate, TimeSpan timeout)
    {
        Stopwatch sw = Stopwatch.StartNew();
        while (!predicate())
        {
            if (sw.Elapsed >= timeout)
            {
                Assert.Fail($"Condition was not satisfied within {timeout.TotalMilliseconds}ms.");
            }

            await Task.Delay(15);
        }
    }

    private sealed class TestTransferNode(
        InMemoryTransferSessionRepository sessionRepo,
        InMemoryDesktopFileStorage fileStorage,
        RecordingPayloadSender payloadSender,
        RecordingTrackerPort trackerPort,
        InMemoryTransferSessionCertificateStore certStore,
        FakeTransferPeerTransport peerTransport,
        InMemoryEventBusForTests eventBus,
        TransferSessionRuntime runtime,
        FileTransferActionHandler actionHandler,
        FileTransferPayloadHandler payloadHandler) : IAsyncDisposable
    {
        public InMemoryTransferSessionRepository SessionRepo { get; } = sessionRepo;
        public InMemoryDesktopFileStorage FileStorage { get; } = fileStorage;
        public RecordingPayloadSender PayloadSender { get; } = payloadSender;
        public RecordingTrackerPort TrackerPort { get; } = trackerPort;
        public InMemoryTransferSessionCertificateStore CertStore { get; } = certStore;
        public FakeTransferPeerTransport PeerTransport { get; } = peerTransport;
        public InMemoryEventBusForTests EventBus { get; } = eventBus;
        public TransferSessionRuntime Runtime { get; } = runtime;
        public FileTransferActionHandler ActionHandler { get; } = actionHandler;
        public FileTransferPayloadHandler PayloadHandler { get; } = payloadHandler;

        public ValueTask DisposeAsync() => Runtime.DisposeAsync();
    }
}

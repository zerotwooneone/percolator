namespace Percolator.ApplicationIntegrationTests.Scenarios;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Google.Protobuf;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Percolator.ApplicationIntegrationTests.Harness;
using Percolator.Apps.FileTransfer.Domain.Common;
using Percolator.Apps.FileTransfer.Domain.ManifestManagement;
using Percolator.Apps.FileTransfer.Domain.SwarmCoordination;
using Percolator.Apps.FileTransfer.Domain.TransferNegotiation;
using Percolator.Apps.FileTransfer.Features.MultiStreamEngine;
using Percolator.Apps.FileTransfer.Features.Storage;
using Percolator.Apps.FileTransfer.Features.TransferNegotiation;
using Percolator.Apps.FileTransfer.Ports;
using Percolator.Domain.Channels.ValueObjects;
using Percolator.Domain.Common;
using Percolator.Domain.Delivery.Hosting;
using Percolator.Domain.Identities;
using Percolator.Domain.Identities.ValueObjects;
using Percolator.Infrastructure.Common;
using Percolator.Infrastructure2.Protos;
using Percolator.PluginSdk;
using PluginDeliveryRoute = Percolator.PluginSdk.DeliveryRoute;

[TestFixture]
public sealed class FileTransferEndToEndIntegrationTests
{
    private TestNetworkTopology _topology = null!;
    private string _testTempRoot = null!;

    [SetUp]
    public void SetUp()
    {
        _topology = new TestNetworkTopology();
        _testTempRoot = Path.Combine(
            Path.GetTempPath(),
            "PercolatorFtE2E",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testTempRoot);
    }

    [TearDown]
    public async Task TearDown()
    {
        await _topology.DisposeAsync().ConfigureAwait(false);

        if (Directory.Exists(_testTempRoot))
        {
            try
            {
                Directory.Delete(_testTempRoot, recursive: true);
            }
            catch
            {
                // Best-effort cleanup on Windows file handles
            }
        }
    }

    // -------------------------------------------------------------------------
    // Scenario 1: 1:1 Direct WebRTC DataChannel (ICE + DTLS + SCTP + UDP STUN)
    // -------------------------------------------------------------------------
    [Test]
    public async Task OneToOne_DirectWebRtcDtlsSctp_2MbMultiFileBundle_Sha256MatchAndZeroOpenSockets()
    {
        var trackerPolicy = new RelayHostingPolicy(
            enabledFeatures: RelayFeatureFlags.DefaultCore | RelayFeatureFlags.SwarmTracker);

        var relay = await _topology.CreateRelayAsync(trackerPolicy).ConfigureAwait(false);
        relay.StunEndpoint.Should().NotBeNull("Exchange UDP STUN responder must be active when SwarmTracker is enabled");

        var alice = await _topology.CreatePersonaAsync("Alice").ConfigureAwait(false);
        var bob = await _topology.CreatePersonaAsync("Bob").ConfigureAwait(false);
        alice.EnableFileTransfers();
        bob.EnableFileTransfers();

        await alice.LinkRelayAsync(relay).ConfigureAwait(false);
        await bob.LinkRelayAsync(relay).ConfigureAwait(false);
        await _topology.PairPersonasAsync(alice, bob, relay).ConfigureAwait(false);

        var aliceTransport = alice.Services.GetRequiredService<ITransferPeerTransport>();
        var bobTransport = bob.Services.GetRequiredService<ITransferPeerTransport>();

        // Assert zero open sockets before transfer starts
        aliceTransport.ActiveConnectionCount.Should().Be(0);
        bobTransport.ActiveConnectionCount.Should().Be(0);

        string aliceSourceDir = CreateSubDir("s1_alice_src");
        string bobDestDir = CreateSubDir("s1_bob_dst");

        string reportRel = "docs/report.bin";
        string clipRel = "media/clip.bin";
        byte[] reportHash = WriteDeterministicFile(aliceSourceDir, reportRel, 1_258_291, seed: 101); // ~1.2 MB
        byte[] clipHash = WriteDeterministicFile(aliceSourceDir, clipRel, 838_861, seed: 202);      // ~0.8 MB (total ~2 MB)

        ChannelId directChannelId = ChannelId.DeterministicForDirect(alice.IdentityId, bob.IdentityId, relay.Endpoint);

        TransferSessionId sessionId;
        using (var scope = alice.Services.CreateScope())
        {
            var actions = scope.ServiceProvider.GetRequiredService<IFileTransferActionHandler>();
            var initResult = await actions.InitiateTransferAsync(
                directChannelId,
                alice.IdentityId,
                [bob.IdentityId],
                RootDirectoryPath.Parse(aliceSourceDir),
                [SanitizedRelativePath.Parse(reportRel), SanitizedRelativePath.Parse(clipRel)],
                DirectWanPolicy.DisabledByDefault,
                RootBundleName.Parse("TwoMegBundle")).ConfigureAwait(false);

            initResult.IsSuccess.Should().BeTrue(initResult.IsFailure ? initResult.Error.Description : null);
            sessionId = initResult.Value;
        }

        // Wait for Bob to receive the real Double Ratchet offer
        TransferSession bobOffer = await WaitForSessionInDbAsync(bob, sessionId, TransferSessionState.PendingConsent, TimeSpan.FromSeconds(15)).ConfigureAwait(false);
        bobOffer.Manifest.TotalFiles.Count.Should().Be(2);

        // Bob accepts into isolated destination directory -> triggers real DataChannelDotnet ICE + DTLS + SCTP
        using (var scope = bob.Services.CreateScope())
        {
            var actions = scope.ServiceProvider.GetRequiredService<IFileTransferActionHandler>();
            var acceptResult = await actions.AcceptTransferAsync(
                sessionId,
                RootDirectoryPath.Parse(bobDestDir)).ConfigureAwait(false);

            acceptResult.IsSuccess.Should().BeTrue(acceptResult.IsFailure ? acceptResult.Error.Description : null);
        }

        // Wait for Bob's runtime session to finish downloading 100% and enter Seeding
        TransferSession bobCompleted = await WaitForRuntimeStateAsync(bob, sessionId, TransferSessionState.Seeding, TimeSpan.FromSeconds(30)).ConfigureAwait(false);
        bobCompleted.IsDownloadComplete.Should().BeTrue();

        // Verify byte-for-byte SHA-256 match on both files
        ComputeFileSha256(Path.Combine(bobDestDir, "docs", "report.bin")).Should().Equal(reportHash);
        ComputeFileSha256(Path.Combine(bobDestDir, "media", "clip.bin")).Should().Equal(clipHash);

        // Fetch swarm rendezvous ID from Alice's DB to verify tracker cleanup
        SwarmRendezvousId rendezvousId;
        using (var scope = alice.Services.CreateScope())
        {
            var transportStore = scope.ServiceProvider.GetRequiredService<ITransferSessionTransportStore>();
            var ctx = await transportStore.GetTransportContextAsync(sessionId).ConfigureAwait(false);
            ctx.Should().NotBeNull();
            rendezvousId = ctx!.SwarmRendezvousId;
        }

        // Stop seeding on both Alice and Bob -> assert 0 open sockets and tracker unregistered immediately
        using (var scope = alice.Services.CreateScope())
        {
            var actions = scope.ServiceProvider.GetRequiredService<IFileTransferActionHandler>();
            await actions.StopSeedingAsync(sessionId).ConfigureAwait(false);
        }

        using (var scope = bob.Services.CreateScope())
        {
            var actions = scope.ServiceProvider.GetRequiredService<IFileTransferActionHandler>();
            await actions.StopSeedingAsync(sessionId).ConfigureAwait(false);
        }

        await WaitForConditionAsync(
            () => aliceTransport.ActiveConnectionCount == 0 && bobTransport.ActiveConnectionCount == 0,
            TimeSpan.FromSeconds(5),
            "ActiveConnectionCount should drop to 0 on both peers after StopSeeding").ConfigureAwait(false);

        aliceTransport.ActiveConnectionCount.Should().Be(0);
        bobTransport.ActiveConnectionCount.Should().Be(0);
        relay.SwarmTrackerRegistry.GetActivePeerCount(rendezvousId.Span).Should().Be(0);
        AssertZeroDiskFiles(relay.BlobStorageDirectory);
    }

    // -------------------------------------------------------------------------
    // Scenario 2: 3-Node Group V2 Swarm & Offline-Author Seeding
    // -------------------------------------------------------------------------
    [Test]
    public async Task GroupV2_ThreeNodeSwarm_OfflineAuthorSeeding_CharlieDownloadsFromBob()
    {
        var trackerPolicy = new RelayHostingPolicy(
            enabledFeatures: RelayFeatureFlags.DefaultCore | RelayFeatureFlags.SwarmTracker);

        var relay = await _topology.CreateRelayAsync(trackerPolicy).ConfigureAwait(false);
        var (alice, bob, charlie, groupChannelId) = await SetupThreeNodeGroupAsync(relay, "Swarm Team").ConfigureAwait(false);

        string aliceSourceDir = CreateSubDir("s2_alice_src");
        string bobDestDir = CreateSubDir("s2_bob_dst");
        string charlieDestDir = CreateSubDir("s2_charlie_dst");

        string partARel = "bundle/partA.bin";
        string partBRel = "bundle/partB.bin";
        byte[] partAHash = WriteDeterministicFile(aliceSourceDir, partARel, 614_400, seed: 303); // 600 KB
        byte[] partBHash = WriteDeterministicFile(aliceSourceDir, partBRel, 409_600, seed: 404); // 400 KB (total 1 MB)

        TransferSessionId sessionId;
        SwarmRendezvousId rendezvousId;
        using (var scope = alice.Services.CreateScope())
        {
            var actions = scope.ServiceProvider.GetRequiredService<IFileTransferActionHandler>();
            var initResult = await actions.InitiateTransferAsync(
                groupChannelId,
                alice.IdentityId,
                [bob.IdentityId, charlie.IdentityId],
                RootDirectoryPath.Parse(aliceSourceDir),
                [SanitizedRelativePath.Parse(partARel), SanitizedRelativePath.Parse(partBRel)],
                DirectWanPolicy.DisabledByDefault,
                RootBundleName.Parse("GroupSwarmBundle")).ConfigureAwait(false);

            initResult.IsSuccess.Should().BeTrue(initResult.IsFailure ? initResult.Error.Description : null);
            sessionId = initResult.Value;

            var transportStore = scope.ServiceProvider.GetRequiredService<ITransferSessionTransportStore>();
            rendezvousId = (await transportStore.GetTransportContextAsync(sessionId).ConfigureAwait(false))!.SwarmRendezvousId;
        }

        // Wait for BOTH Bob and Charlie to receive the Group V2 SenderKey offer
        await WaitForSessionInDbAsync(bob, sessionId, TransferSessionState.PendingConsent, TimeSpan.FromSeconds(15)).ConfigureAwait(false);
        await WaitForSessionInDbAsync(charlie, sessionId, TransferSessionState.PendingConsent, TimeSpan.FromSeconds(15)).ConfigureAwait(false);

        // Bob accepts and downloads 100% from Alice while staying unlocked and seeding
        using (var scope = bob.Services.CreateScope())
        {
            var actions = scope.ServiceProvider.GetRequiredService<IFileTransferActionHandler>();
            var acceptResult = await actions.AcceptTransferAsync(sessionId, RootDirectoryPath.Parse(bobDestDir)).ConfigureAwait(false);
            acceptResult.IsSuccess.Should().BeTrue(acceptResult.IsFailure ? acceptResult.Error.Description : null);
        }

        await WaitForRuntimeStateAsync(bob, sessionId, TransferSessionState.Seeding, TimeSpan.FromSeconds(30)).ConfigureAwait(false);
        ComputeFileSha256(Path.Combine(bobDestDir, "bundle", "partA.bin")).Should().Equal(partAHash);
        ComputeFileSha256(Path.Combine(bobDestDir, "bundle", "partB.bin")).Should().Equal(partBHash);

        // Capture Bob's persisted session cert fingerprint so we verify Charlie pinned Bob's cert
        CertSha256Fingerprint bobCertFp;
        using (var bobCert = await bob.Services.GetRequiredService<ITransferSessionCertificateStore>().GetAsync(sessionId).ConfigureAwait(false))
        {
            bobCert.Should().NotBeNull();
            bobCertFp = bobCert!.Fingerprint;
        }

        // Alice goes COMPLETELY OFFLINE before Charlie accepts
        await alice.DisposeAsync().ConfigureAwait(false);

        // Confirm Alice unregistered while Bob remains registered as active Seeder on Exchange tracker
        await WaitForConditionAsync(
            () => relay.SwarmTrackerRegistry.GetActivePeerCount(rendezvousId.Span) == 1,
            TimeSpan.FromSeconds(5),
            "Exchange tracker should contain only Bob after Alice goes offline").ConfigureAwait(false);

        // Charlie now accepts -> announces PeerJoin over Group V2 -> pins Bob's DTLS cert -> downloads 100% directly from Bob
        using (var scope = charlie.Services.CreateScope())
        {
            var actions = scope.ServiceProvider.GetRequiredService<IFileTransferActionHandler>();
            var acceptResult = await actions.AcceptTransferAsync(sessionId, RootDirectoryPath.Parse(charlieDestDir)).ConfigureAwait(false);
            acceptResult.IsSuccess.Should().BeTrue(acceptResult.IsFailure ? acceptResult.Error.Description : null);
        }

        TransferSession charlieCompleted = await WaitForRuntimeStateAsync(charlie, sessionId, TransferSessionState.Seeding, TimeSpan.FromSeconds(30)).ConfigureAwait(false);
        charlieCompleted.IsDownloadComplete.Should().BeTrue();

        var charlieRuntime = charlie.Services.GetRequiredService<ITransferSessionRuntime>();
        charlieRuntime.GetPinnedPeerCertificate(sessionId, bob.IdentityId).Should().Be(bobCertFp);

        // Verify Alice == Bob == Charlie byte-for-byte SHA-256
        ComputeFileSha256(Path.Combine(charlieDestDir, "bundle", "partA.bin")).Should().Equal(partAHash);
        ComputeFileSha256(Path.Combine(charlieDestDir, "bundle", "partB.bin")).Should().Equal(partBHash);
        AssertZeroDiskFiles(relay.BlobStorageDirectory);
    }

    // -------------------------------------------------------------------------
    // Scenario 3: Group V2 Eviction (Epoch E -> E+1) Revokes Cert & Drops Peer
    // -------------------------------------------------------------------------
    [Test]
    public async Task GroupV2_EvictCharlie_EpochRotation_ImmediatelyRevokesCertAndRejectsRequests()
    {
        var trackerPolicy = new RelayHostingPolicy(
            enabledFeatures: RelayFeatureFlags.DefaultCore | RelayFeatureFlags.SwarmTracker);

        var relay = await _topology.CreateRelayAsync(trackerPolicy).ConfigureAwait(false);
        var (alice, bob, charlie, groupChannelId) = await SetupThreeNodeGroupAsync(relay, "Eviction Group").ConfigureAwait(false);

        string aliceSourceDir = CreateSubDir("s3_alice_src");
        string bobDestDir = CreateSubDir("s3_bob_dst");
        string charlieDestDir = CreateSubDir("s3_charlie_dst");

        string dataRel = "shared.bin";
        WriteDeterministicFile(aliceSourceDir, dataRel, 262_144, seed: 505); // 256 KB

        TransferSessionId sessionId;
        using (var scope = alice.Services.CreateScope())
        {
            var actions = scope.ServiceProvider.GetRequiredService<IFileTransferActionHandler>();
            var initResult = await actions.InitiateTransferAsync(
                groupChannelId,
                alice.IdentityId,
                [bob.IdentityId, charlie.IdentityId],
                RootDirectoryPath.Parse(aliceSourceDir),
                [SanitizedRelativePath.Parse(dataRel)],
                DirectWanPolicy.DisabledByDefault,
                RootBundleName.Parse("EvictBundle")).ConfigureAwait(false);

            initResult.IsSuccess.Should().BeTrue(initResult.IsFailure ? initResult.Error.Description : null);
            sessionId = initResult.Value;
        }

        await WaitForSessionInDbAsync(bob, sessionId, TransferSessionState.PendingConsent, TimeSpan.FromSeconds(15)).ConfigureAwait(false);
        await WaitForSessionInDbAsync(charlie, sessionId, TransferSessionState.PendingConsent, TimeSpan.FromSeconds(15)).ConfigureAwait(false);

        // Bob accepts & completes so Bob becomes an active Seeder
        using (var scope = bob.Services.CreateScope())
        {
            var actions = scope.ServiceProvider.GetRequiredService<IFileTransferActionHandler>();
            await actions.AcceptTransferAsync(sessionId, RootDirectoryPath.Parse(bobDestDir)).ConfigureAwait(false);
        }
        await WaitForRuntimeStateAsync(bob, sessionId, TransferSessionState.Seeding, TimeSpan.FromSeconds(25)).ConfigureAwait(false);

        // Charlie joins and connects to Bob over Group V2 so Bob pins Charlie's certificate and opens a DataChannel
        using (var scope = charlie.Services.CreateScope())
        {
            var actions = scope.ServiceProvider.GetRequiredService<IFileTransferActionHandler>();
            await actions.AcceptTransferAsync(sessionId, RootDirectoryPath.Parse(charlieDestDir)).ConfigureAwait(false);
        }

        var bobRuntime = bob.Services.GetRequiredService<ITransferSessionRuntime>();
        await WaitForConditionAsync(
            () => bobRuntime.GetPinnedPeerCertificate(sessionId, charlie.IdentityId) != null &&
                  bobRuntime.GetConnectedPeers(sessionId).Contains(charlie.IdentityId),
            TimeSpan.FromSeconds(25),
            "Bob should pin Charlie's cert and hold an active peer channel before eviction").ConfigureAwait(false);

        // Alice evicts Charlie (Epoch E -> E+1) and emits real SystemNoticeType.MemberLeft over rotated Group V2 SenderKey
        await alice.EvictGroupMemberAsync(relay, groupChannelId, charlie, [alice, bob]).ConfigureAwait(false);
        await bob.WaitForGroupReceiverSessionAsync(groupChannelId, alice.IdentityId, TimeSpan.FromSeconds(10), minKeyId: 2).ConfigureAwait(false);
        await alice.SendMemberLeftSystemNoticeAsync(groupChannelId, charlie.IdentityId).ConfigureAwait(false);

        // Bob's ChatPayloadHandler publishes GroupMemberRemovedApplicationEvent -> TransferSessionRuntime immediately revokes Charlie
        await WaitForConditionAsync(
            () => bobRuntime.GetPinnedPeerCertificate(sessionId, charlie.IdentityId) == null &&
                  !bobRuntime.GetConnectedPeers(sessionId).Contains(charlie.IdentityId),
            TimeSpan.FromSeconds(10),
            "Bob's runtime must revoke Charlie's cert and drop Charlie's channel upon GroupMemberRemovedApplicationEvent").ConfigureAwait(false);

        bobRuntime.GetPinnedPeerCertificate(sessionId, charlie.IdentityId).Should().BeNull();
        bobRuntime.GetConnectedPeers(sessionId).Should().NotContain(charlie.IdentityId);

        // Any subsequent ICE / reconnect attempt from evicted Charlie must be rejected immediately
        var forgedSignal = new FileTransferIceSignalPayload(
            sessionId,
            IceSignalKind.SdpOffer,
            "v=0\r\nm=application 9 UDP/DTLS/SCTP webrtc-datachannel\r\n",
            CandidateMid: null,
            DtlsFingerprintSha256: null,
            TargetPeerId: bob.IdentityId);

        DomainResult rejected = await bobRuntime.HandleInboundIceSignalAsync(
            sessionId,
            charlie.IdentityId,
            forgedSignal,
            (_, _) => ValueTask.CompletedTask).ConfigureAwait(false);

        rejected.IsFailure.Should().BeTrue();
        rejected.Error.Code.Should().Be("Transfer.UnauthorizedOrRevokedPeer");
    }

    // -------------------------------------------------------------------------
    // Scenario 4: Optional Zero-Disk TransferPipe Fallback & Quota Enforcement
    // -------------------------------------------------------------------------
    [Test]
    public async Task TransferPipeFallback_EnabledZeroDiskAndQuota_AndDisabledReturnsCleanError()
    {
        // Part A: Exchange with TransferPipe enabled -> direct ICE blocked -> succeeds via TLS 1.3 mTLS pipe with 0 bytes on disk
        var pipePolicy = RelayHostingPolicy.Default with
        {
            EnabledFeatures = RelayFeatureFlags.DefaultCore | RelayFeatureFlags.SwarmTracker | RelayFeatureFlags.TransferPipe,
            MaxConcurrentPipes = 4,
            MaxBytesPerSession = 2 * 1024 * 1024,
            MaxBytesPerSecondPerPipe = 50 * 1024 * 1024,
            PipeIdleTimeout = TimeSpan.FromSeconds(15)
        };

        var pipeRelay = await _topology.CreateRelayAsync(pipePolicy).ConfigureAwait(false);
        var alice = await _topology.CreatePersonaAsync("AlicePipe", blockDirectIce: true).ConfigureAwait(false);
        var bob = await _topology.CreatePersonaAsync("BobPipe", blockDirectIce: true).ConfigureAwait(false);
        alice.EnableFileTransfers();
        bob.EnableFileTransfers();

        await alice.LinkRelayAsync(pipeRelay).ConfigureAwait(false);
        await bob.LinkRelayAsync(pipeRelay).ConfigureAwait(false);
        await _topology.PairPersonasAsync(alice, bob, pipeRelay).ConfigureAwait(false);

        string aliceSourceDir = CreateSubDir("s4_alice_src");
        string bobDestDir = CreateSubDir("s4_bob_dst");
        string fileRel = "fallback/payload.bin";
        byte[] expectedHash = WriteDeterministicFile(aliceSourceDir, fileRel, 524_288, seed: 606); // 512 KB

        ChannelId pipeChannelId = ChannelId.DeterministicForDirect(alice.IdentityId, bob.IdentityId, pipeRelay.Endpoint);

        TransferSessionId sessionId;
        using (var scope = alice.Services.CreateScope())
        {
            var actions = scope.ServiceProvider.GetRequiredService<IFileTransferActionHandler>();
            var initResult = await actions.InitiateTransferAsync(
                pipeChannelId,
                alice.IdentityId,
                [bob.IdentityId],
                RootDirectoryPath.Parse(aliceSourceDir),
                [SanitizedRelativePath.Parse(fileRel)],
                DirectWanPolicy.DisabledByDefault,
                RootBundleName.Parse("PipeFallbackBundle")).ConfigureAwait(false);

            initResult.IsSuccess.Should().BeTrue(initResult.IsFailure ? initResult.Error.Description : null);
            sessionId = initResult.Value;
        }

        await WaitForSessionInDbAsync(bob, sessionId, TransferSessionState.PendingConsent, TimeSpan.FromSeconds(15)).ConfigureAwait(false);

        using (var scope = bob.Services.CreateScope())
        {
            var actions = scope.ServiceProvider.GetRequiredService<IFileTransferActionHandler>();
            var acceptResult = await actions.AcceptTransferAsync(sessionId, RootDirectoryPath.Parse(bobDestDir)).ConfigureAwait(false);
            acceptResult.IsSuccess.Should().BeTrue(acceptResult.IsFailure ? acceptResult.Error.Description : null);
        }

        // Direct ICE is blocked on Alice -> Alice automatically falls back to TransferPipe via Double Ratchet PipeRendezvous
        TransferSession bobCompleted = await WaitForRuntimeStateAsync(bob, sessionId, TransferSessionState.Seeding, TimeSpan.FromSeconds(30)).ConfigureAwait(false);
        bobCompleted.IsDownloadComplete.Should().BeTrue();
        ComputeFileSha256(Path.Combine(bobDestDir, "fallback", "payload.bin")).Should().Equal(expectedHash);

        // Zero bytes written to Exchange disk!
        AssertZeroDiskFiles(pipeRelay.BlobStorageDirectory);

        // Part A2: Verify Exchange MaxBytesPerSession quota enforcement aborts oversized pipe transfer cleanly
        pipeRelay.Policy = pipePolicy with { MaxBytesPerSession = 128 * 1024 };
        string bigSourceDir = CreateSubDir("s4_big_src");
        string bigDestDir = CreateSubDir("s4_big_dst");
        WriteDeterministicFile(bigSourceDir, "quota.bin", 524_288, seed: 607); // 512 KB > 128 KB cap

        TransferSessionId quotaSessionId;
        using (var scope = alice.Services.CreateScope())
        {
            var actions = scope.ServiceProvider.GetRequiredService<IFileTransferActionHandler>();
            var initResult = await actions.InitiateTransferAsync(
                pipeChannelId,
                alice.IdentityId,
                [bob.IdentityId],
                RootDirectoryPath.Parse(bigSourceDir),
                [SanitizedRelativePath.Parse("quota.bin")],
                DirectWanPolicy.DisabledByDefault,
                RootBundleName.Parse("QuotaBundle")).ConfigureAwait(false);

            initResult.IsSuccess.Should().BeTrue();
            quotaSessionId = initResult.Value;
        }

        await WaitForSessionInDbAsync(bob, quotaSessionId, TransferSessionState.PendingConsent, TimeSpan.FromSeconds(15)).ConfigureAwait(false);

        using (var scope = bob.Services.CreateScope())
        {
            var actions = scope.ServiceProvider.GetRequiredService<IFileTransferActionHandler>();
            await actions.AcceptTransferAsync(quotaSessionId, RootDirectoryPath.Parse(bigDestDir)).ConfigureAwait(false);
        }

        // Give the pipe enough time to hit the 128 KB byte cap and terminate cleanly
        await Task.Delay(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        var bobRuntime = bob.Services.GetRequiredService<ITransferSessionRuntime>();
        bobRuntime.GetActiveSession(quotaSessionId)!.IsDownloadComplete.Should().BeFalse("Exchange MaxBytesPerSession quota must terminate oversized pipe stream");
        AssertZeroDiskFiles(pipeRelay.BlobStorageDirectory);

        // Part B: Exchange with TransferPipe DISABLED (default) -> direct ICE failure returns clean DIRECT_NAT_TRAVERSAL_FAILED
        var defaultTrackerPolicy = new RelayHostingPolicy(
            enabledFeatures: RelayFeatureFlags.DefaultCore | RelayFeatureFlags.SwarmTracker);

        var noPipeRelay = await _topology.CreateRelayAsync(defaultTrackerPolicy).ConfigureAwait(false);
        var aliceNoPipe = await _topology.CreatePersonaAsync("AliceNoPipe", blockDirectIce: true).ConfigureAwait(false);
        var bobNoPipe = await _topology.CreatePersonaAsync("BobNoPipe", blockDirectIce: true).ConfigureAwait(false);
        aliceNoPipe.EnableFileTransfers();
        bobNoPipe.EnableFileTransfers();

        await aliceNoPipe.LinkRelayAsync(noPipeRelay).ConfigureAwait(false);
        await bobNoPipe.LinkRelayAsync(noPipeRelay).ConfigureAwait(false);
        await _topology.PairPersonasAsync(aliceNoPipe, bobNoPipe, noPipeRelay).ConfigureAwait(false);

        string noPipeSrcDir = CreateSubDir("s4_nopipe_src");
        WriteDeterministicFile(noPipeSrcDir, "small.bin", 65_536, seed: 608);

        ChannelId noPipeChannel = ChannelId.DeterministicForDirect(aliceNoPipe.IdentityId, bobNoPipe.IdentityId, noPipeRelay.Endpoint);
        TransferSessionId noPipeSessionId;
        using (var scope = aliceNoPipe.Services.CreateScope())
        {
            var actions = scope.ServiceProvider.GetRequiredService<IFileTransferActionHandler>();
            var initResult = await actions.InitiateTransferAsync(
                noPipeChannel,
                aliceNoPipe.IdentityId,
                [bobNoPipe.IdentityId],
                RootDirectoryPath.Parse(noPipeSrcDir),
                [SanitizedRelativePath.Parse("small.bin")],
                DirectWanPolicy.DisabledByDefault,
                RootBundleName.Parse("NoPipeBundle")).ConfigureAwait(false);

            initResult.IsSuccess.Should().BeTrue();
            noPipeSessionId = initResult.Value;
        }

        // Pin Bob's cert on Alice and invoke InitiateDirectConnectionAsync -> must return DIRECT_NAT_TRAVERSAL_FAILED cleanly
        var aliceNoPipeRuntime = aliceNoPipe.Services.GetRequiredService<ITransferSessionRuntime>();
        await aliceNoPipeRuntime.RegisterPinnedPeerCertificateAsync(
            noPipeSessionId,
            bobNoPipe.IdentityId,
            CertSha256Fingerprint.FromBytes(RandomNumberGenerator.GetBytes(32))).ConfigureAwait(false);

        DomainResult directFailure = await aliceNoPipeRuntime.InitiateDirectConnectionAsync(
            noPipeSessionId,
            bobNoPipe.IdentityId,
            (_, _) => ValueTask.CompletedTask).ConfigureAwait(false);

        directFailure.IsFailure.Should().BeTrue();
        directFailure.Error.Code.Should().Be(TransferTransportErrorCodes.DirectNatTraversalFailed);

        // Also verify direct pipe open against a TransferPipe-disabled Exchange returns DIRECT_NAT_TRAVERSAL_FAILED
        DomainResult pipeDisabledResult = await aliceNoPipeRuntime.ConnectViaTransferPipeAsync(
            noPipeSessionId,
            bobNoPipe.IdentityId,
            noPipeRelay.Endpoint,
            PipeRendezvousToken.Generate(),
            isTlsServer: true).ConfigureAwait(false);

        pipeDisabledResult.IsFailure.Should().BeTrue();
        pipeDisabledResult.Error.Code.Should().Be(TransferTransportErrorCodes.DirectNatTraversalFailed);
    }

    // -------------------------------------------------------------------------
    // Scenario 5: Vault Lock Teardown (< 1s) & Resume With Vault-Persisted Cert
    // -------------------------------------------------------------------------
    [Test]
    public async Task VaultLockTeardownAndResume_ClosesSocketsWithin1s_UnregistersTracker_AndResumesWithPersistedCert()
    {
        var trackerPolicy = new RelayHostingPolicy(
            enabledFeatures: RelayFeatureFlags.DefaultCore | RelayFeatureFlags.SwarmTracker);

        var relay = await _topology.CreateRelayAsync(trackerPolicy).ConfigureAwait(false);

        string bobVaultPath = Path.Combine(_testTempRoot, "bob.vault.db");
        var alice = await _topology.CreatePersonaAsync("AliceVault").ConfigureAwait(false);
        var bob = await _topology.CreatePersonaAsync("BobVault", sqliteFilePath: bobVaultPath).ConfigureAwait(false);
        alice.EnableFileTransfers();
        bob.EnableFileTransfers();

        await alice.LinkRelayAsync(relay).ConfigureAwait(false);
        await bob.LinkRelayAsync(relay).ConfigureAwait(false);
        await _topology.PairPersonasAsync(alice, bob, relay).ConfigureAwait(false);

        string aliceSourceDir = CreateSubDir("s5_alice_src");
        string bobDestDir = CreateSubDir("s5_bob_dst");
        string fileRel = "resumable.bin";
        int fileSizeBytes = 2 * 1024 * 1024; // 2 MB = 32 chunks of 64 KB
        byte[] expectedHash = WriteDeterministicFile(aliceSourceDir, fileRel, fileSizeBytes, seed: 707);

        // Throttle Bob's download rate to 192 KB/s (~3 chunks/sec) so we deterministically lock Bob's vault mid-transfer
        var bobLimiter = bob.Services.GetRequiredService<ITransferRateLimiter>();
        await bobLimiter.SetGlobalSpeedCapsAsync(
            BandwidthLimit.Capped(TransferBandwidth.FromBytesPerSecond(196_608)),
            BandwidthLimit.Unlimited).ConfigureAwait(false);

        ChannelId directChannelId = ChannelId.DeterministicForDirect(alice.IdentityId, bob.IdentityId, relay.Endpoint);

        TransferSessionId sessionId;
        SwarmRendezvousId rendezvousId;
        using (var scope = alice.Services.CreateScope())
        {
            var actions = scope.ServiceProvider.GetRequiredService<IFileTransferActionHandler>();
            var initResult = await actions.InitiateTransferAsync(
                directChannelId,
                alice.IdentityId,
                [bob.IdentityId],
                RootDirectoryPath.Parse(aliceSourceDir),
                [SanitizedRelativePath.Parse(fileRel)],
                DirectWanPolicy.DisabledByDefault,
                RootBundleName.Parse("VaultResumeBundle")).ConfigureAwait(false);

            initResult.IsSuccess.Should().BeTrue();
            sessionId = initResult.Value;

            var transportStore = scope.ServiceProvider.GetRequiredService<ITransferSessionTransportStore>();
            rendezvousId = (await transportStore.GetTransportContextAsync(sessionId).ConfigureAwait(false))!.SwarmRendezvousId;
        }

        await WaitForSessionInDbAsync(bob, sessionId, TransferSessionState.PendingConsent, TimeSpan.FromSeconds(15)).ConfigureAwait(false);

        using (var scope = bob.Services.CreateScope())
        {
            var actions = scope.ServiceProvider.GetRequiredService<IFileTransferActionHandler>();
            var acceptResult = await actions.AcceptTransferAsync(sessionId, RootDirectoryPath.Parse(bobDestDir)).ConfigureAwait(false);
            acceptResult.IsSuccess.Should().BeTrue();
        }

        // Capture Bob's original vault-persisted ECDSA P-256 certificate fingerprint
        CertSha256Fingerprint originalBobCertFp;
        using (var certMat = await bob.Services.GetRequiredService<ITransferSessionCertificateStore>().GetAsync(sessionId).ConfigureAwait(false))
        {
            certMat.Should().NotBeNull();
            originalBobCertFp = certMat!.Fingerprint;
        }

        // Wait until Bob has genuinely verified at least 4 chunks over real WebRTC (and is still mid-transfer)
        var bobRuntime = bob.Services.GetRequiredService<ITransferSessionRuntime>();
        var bobTransport = bob.Services.GetRequiredService<ITransferPeerTransport>();

        await WaitForConditionAsync(
            () =>
            {
                TransferSession? s = bobRuntime.GetActiveSession(sessionId);
                return s != null && s.LocalBitfield.PopCount >= 4 && !s.IsDownloadComplete;
            },
            TimeSpan.FromSeconds(25),
            "Bob should verify at least 4 chunks mid-transfer before Vault Lock").ConfigureAwait(false);

        int chunksBeforeLock = bobRuntime.GetActiveSession(sessionId)!.LocalBitfield.PopCount;
        chunksBeforeLock.Should().BeGreaterThanOrEqualTo(4);
        relay.SwarmTrackerRegistry.GetActivePeerCount(rendezvousId.Span).Should().Be(2, "both Alice and Bob must be registered on tracker mid-transfer");

        // Trigger mid-transfer Persona Vault Lock -> must close ICE/sockets within 1s and unregister Bob from tracker
        var lockSw = Stopwatch.StartNew();
        await bob.DisposeAsync().ConfigureAwait(false);
        lockSw.Stop();

        lockSw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(1), "Vault Lock teardown must complete within 1 second");
        bobTransport.ActiveConnectionCount.Should().Be(0);
        await WaitForConditionAsync(
            () => relay.SwarmTrackerRegistry.GetActivePeerCount(rendezvousId.Span) == 1,
            TimeSpan.FromSeconds(5),
            "Bob must unregister from Exchange tracker upon Vault Lock").ConfigureAwait(false);

        // Record Alice's uploaded bytes before Bob resumes so we prove already-verified chunks are skipped
        var aliceRuntime = alice.Services.GetRequiredService<ITransferSessionRuntime>();
        long aliceUploadedBeforeResume = aliceRuntime.GetActiveSession(sessionId)!.UploadedBytes.Bytes;

        // Unlock Bob against the EXACT SAME SQLite vault file + download directory (full speed)
        var bobUnlocked = await TestPersonaNode.UnlockFromVaultFileAsync(bob.IdentityId, "BobVault", bobVaultPath).ConfigureAwait(false);
        _topology.TrackPersona(bobUnlocked);
        bobUnlocked.EnableFileTransfers();

        // Verify vault-persisted ECDSA P-256 session cert matches byte-for-byte across lock/unlock
        using (var unlockedCert = await bobUnlocked.Services.GetRequiredService<ITransferSessionCertificateStore>().GetAsync(sessionId).ConfigureAwait(false))
        {
            unlockedCert.Should().NotBeNull();
            unlockedCert!.Fingerprint.Should().Be(originalBobCertFp);
        }

        // Resume transfer -> scans .percolator-part, skips already-verified chunks via ResumptionBitfield, finishes 100%
        using (var scope = bobUnlocked.Services.CreateScope())
        {
            var actions = scope.ServiceProvider.GetRequiredService<IFileTransferActionHandler>();
            var resumeResult = await actions.ResumeTransferAsync(sessionId).ConfigureAwait(false);
            resumeResult.IsSuccess.Should().BeTrue(resumeResult.IsFailure ? resumeResult.Error.Description : null);
        }

        var bobUnlockedRuntime = bobUnlocked.Services.GetRequiredService<ITransferSessionRuntime>();
        var bobUnlockedTransport = bobUnlocked.Services.GetRequiredService<ITransferPeerTransport>();

        int chunksImmediatelyOnResume = bobUnlockedRuntime.GetActiveSession(sessionId)!.LocalBitfield.PopCount;
        chunksImmediatelyOnResume.Should().BeGreaterThanOrEqualTo(chunksBeforeLock, "ResumptionBitfield must preserve all previously verified chunks from .percolator-part");

        TransferSession bobFinal = await WaitForRuntimeStateAsync(bobUnlocked, sessionId, TransferSessionState.Seeding, TimeSpan.FromSeconds(30)).ConfigureAwait(false);
        bobFinal.IsDownloadComplete.Should().BeTrue();
        ComputeFileSha256(Path.Combine(bobDestDir, fileRel)).Should().Equal(expectedHash);

        long aliceUploadedDeltaOnResume = aliceRuntime.GetActiveSession(sessionId)!.UploadedBytes.Bytes - aliceUploadedBeforeResume;
        aliceUploadedDeltaOnResume.Should().BeLessThan(fileSizeBytes, "already-verified chunks must not be re-transmitted on resume");

        // Now trigger Vault Lock WHILE SEEDING -> must close within 1s and unregister Bob from tracker
        relay.SwarmTrackerRegistry.GetActivePeerCount(rendezvousId.Span).Should().Be(2);

        var seedingLockSw = Stopwatch.StartNew();
        await bobUnlocked.DisposeAsync().ConfigureAwait(false);
        seedingLockSw.Stop();

        seedingLockSw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(1), "Vault Lock teardown while seeding must complete within 1 second");
        bobUnlockedTransport.ActiveConnectionCount.Should().Be(0);
        await WaitForConditionAsync(
            () => relay.SwarmTrackerRegistry.GetActivePeerCount(rendezvousId.Span) == 1,
            TimeSpan.FromSeconds(5),
            "Bob must unregister from tracker upon Vault Lock while seeding").ConfigureAwait(false);
    }

    // -------------------------------------------------------------------------
    // Scenario 6: Integrity & Security (Tampered Chunk, Spoofed Cert, Path Traversal)
    // -------------------------------------------------------------------------
    [Test]
    public async Task IntegrityAndSecurity_TamperedChunkZeroDiskWrite_MismatchedCertRejected_PathTraversalRejected()
    {
        var pipePolicy = RelayHostingPolicy.Default with
        {
            EnabledFeatures = RelayFeatureFlags.DefaultCore | RelayFeatureFlags.SwarmTracker | RelayFeatureFlags.TransferPipe
        };

        var relay = await _topology.CreateRelayAsync(pipePolicy).ConfigureAwait(false);
        var alice = await _topology.CreatePersonaAsync("AliceSec").ConfigureAwait(false);
        var bob = await _topology.CreatePersonaAsync("BobSec").ConfigureAwait(false);
        alice.EnableFileTransfers();
        bob.EnableFileTransfers();

        await alice.LinkRelayAsync(relay).ConfigureAwait(false);
        await bob.LinkRelayAsync(relay).ConfigureAwait(false);
        await _topology.PairPersonasAsync(alice, bob, relay).ConfigureAwait(false);

        // ---------------------------------------------------------------------
        // 6A. Tampered chunk fails SHA-256 Merkle verification, chokes peer,
        //     and writes ZERO bytes to disk (INV-VERIFY-BEFORE-WRITE)
        // ---------------------------------------------------------------------
        string secDestDir = CreateSubDir("s6_tamper_dst");
        byte[] validChunkBytes = RandomNumberGenerator.GetBytes(65_536);
        ChunkHash validLeaf = MerkleTreeBuilder.HashLeaf(validChunkBytes);
        MerkleTreeIndex tree = MerkleTreeBuilder.BuildFromLeaves([validLeaf]);
        var tamperSessionId = TransferSessionId.New();
        var clock = new SystemDateTimeProvider();
        var manifest = TransferManifest.Create(
            tamperSessionId,
            RootBundleName.Parse("TamperBundle"),
            ChunkSizeBytes.FromBytes(65_536),
            tree,
            [new FileEntry(FileIndex.FromInt(0), SanitizedRelativePath.Parse("secret.bin"), FileSizeBytes.FromBytes(65_536), BundleByteOffset.FromOffset(0), FileLastModifiedUtc.FromDateTimeOffset(clock.UtcNow))]);

        var receiverSession = TransferSession.CreateIncomingStaged(tamperSessionId, alice.IdentityId, manifest, SeedRatio.Unlimited, clock);
        receiverSession.AcceptTransfer(SelectedFileIndices.All(manifest.TotalFiles), clock);
        receiverSession.RegisterSwarmMember(alice.IdentityId, Bitfield.Full(manifest.TotalChunks), clock, PeerChokeStatus.Unchoked, PeerChokeStatus.Unchoked);

        var fileStorage = bob.Services.GetRequiredService<IDesktopFileStorage>();
        var rateLimiter = bob.Services.GetRequiredService<ITransferRateLimiter>();
        var scheduler = new ChunkPipelineScheduler();
        scheduler.TrySchedule(alice.IdentityId, ChunkIndex.FromInt(0)).Should().BeTrue();
        var coordinator = new TransferSessionCoordinator(scheduler, new DiskChunkAssembler(fileStorage), clock, rateLimiter);

        byte[] tamperedBytes = (byte[])validChunkBytes.Clone();
        tamperedBytes[1234] ^= 0xFF; // Flip bits to corrupt Merkle leaf hash

        var spyStream = new RecordingTransferStream();
        DomainResult tamperResult = await coordinator.HandleIncomingChunkDataAsync(
            receiverSession,
            alice.IdentityId,
            new ChunkDataFrame(ChunkIndex.FromInt(0), PlaintextChunkData.FromBytes(tamperedBytes)),
            spyStream,
            RootDirectoryPath.Parse(secDestDir)).ConfigureAwait(false);

        tamperResult.IsFailure.Should().BeTrue();
        tamperResult.Error.Code.Should().Be("MultiStream.CorruptedChunk");
        coordinator.GetPeerOffenses(alice.IdentityId).Should().BeGreaterThanOrEqualTo(1);
        receiverSession.SwarmMembers.Single(m => m.PeerId == alice.IdentityId).WeChokingPeer.Should().Be(PeerChokeStatus.Choked);
        spyStream.WasDisposed.Should().BeTrue("offending peer stream must be terminated");

        // Zero bytes written to disk (neither final file nor .percolator-part created)
        Directory.GetFiles(secDestDir, "*", SearchOption.AllDirectories).Should().BeEmpty("tampered chunk must write 0 bytes to disk");

        // ---------------------------------------------------------------------
        // 6B. Spoofed / mismatched DTLS or TLS cert fingerprint rejected immediately
        // ---------------------------------------------------------------------
        var spoofSessionId = TransferSessionId.New();
        var rendezvousToken = PipeRendezvousToken.Generate();
        var authToken = StreamAuthToken.Generate();
        var wrongExpectedCert = CertSha256Fingerprint.FromBytes(RandomNumberGenerator.GetBytes(32));

        CertSha256Fingerprint aliceRealCert;
        using (var matA = await alice.Services.GetRequiredService<ITransferSessionCertificateStore>().GetOrCreateAsync(spoofSessionId).ConfigureAwait(false))
        {
            aliceRealCert = matA.Fingerprint;
        }

        var aliceTransport = alice.Services.GetRequiredService<ITransferPeerTransport>();
        var bobTransport = bob.Services.GetRequiredService<ITransferPeerTransport>();

        var serverPipeTask = aliceTransport.OpenRelayedPipeChannelAsync(new RelayedPipeConnectionOptions(
            spoofSessionId,
            alice.IdentityId,
            bob.IdentityId,
            relay.Endpoint,
            rendezvousToken,
            authToken,
            IsTlsServer: true,
            ExpectedRemoteCertSha256: wrongExpectedCert,
            ExchangeSupportsTransferPipe: true,
            HandshakeTimeout: TimeSpan.FromSeconds(10))).AsTask();

        var clientPipeTask = bobTransport.OpenRelayedPipeChannelAsync(new RelayedPipeConnectionOptions(
            spoofSessionId,
            bob.IdentityId,
            alice.IdentityId,
            relay.Endpoint,
            rendezvousToken,
            authToken,
            IsTlsServer: false,
            ExpectedRemoteCertSha256: aliceRealCert,
            ExchangeSupportsTransferPipe: true,
            HandshakeTimeout: TimeSpan.FromSeconds(10))).AsTask();

        var serverAct = async () => await serverPipeTask.ConfigureAwait(false);
        var serverEx = await serverAct.Should().ThrowAsync<TransferTransportException>();
        serverEx.Which.ErrorCode.Should().Be(TransferTransportErrorCodes.DtlsFingerprintMismatch);

        // Ensure client side also fails/unwinds cleanly
        var clientAct = async () => await clientPipeTask.ConfigureAwait(false);
        await clientAct.Should().ThrowAsync<Exception>();

        // ---------------------------------------------------------------------
        // 6C. Path traversal manifest ("../escape.txt") sent over real wire is
        //     rejected and creates ZERO files anywhere on disk
        // ---------------------------------------------------------------------
        var evilSessionId = TransferSessionId.New();
        byte[] dummyLeaf = SHA256.HashData("evil"u8);

        var evilManifestProto = new TransferManifestProto
        {
            ManifestId = ByteString.CopyFrom(evilSessionId.ToByteArray()),
            RootName = "EvilBundle",
            TotalSizeBytes = 4,
            ChunkSizeBytes = 65_536,
            MerkleRoot = ByteString.CopyFrom(dummyLeaf),
            ConcatenatedLeaves = ByteString.CopyFrom(dummyLeaf)
        };
        evilManifestProto.Files.Add(new FileEntryProto
        {
            Index = 0,
            RelativePath = "../escape.txt",
            FileSizeBytes = 4,
            StartOffset = 0,
            LastModifiedUnixSeconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
        });

        var evilOfferProto = new FileTransferOfferProto
        {
            TransferSessionId = ByteString.CopyFrom(evilSessionId.ToByteArray()),
            Summary = new TransferBundleSummaryProto
            {
                RootName = "EvilBundle",
                TotalSizeBytes = 4,
                TotalFiles = 1,
                ChunkSizeBytes = 65_536,
                TotalChunks = 1,
                MerkleRoot = ByteString.CopyFrom(dummyLeaf)
            },
            Manifest = new ManifestDistributionProto
            {
                Kind = 1,
                InlineManifestData = ByteString.CopyFrom(evilManifestProto.ToByteArray())
            },
            SwarmRendezvousId = ByteString.CopyFrom(RandomNumberGenerator.GetBytes(32)),
            StreamAuthToken = ByteString.CopyFrom(RandomNumberGenerator.GetBytes(32)),
            SenderCertSha256 = ByteString.CopyFrom(aliceRealCert.Span),
            IsGroupTransfer = false
        };

        byte[] evilBody = evilOfferProto.ToByteArray();
        byte[] framedEvilOffer = new byte[1 + evilBody.Length];
        framedEvilOffer[0] = (byte)FileTransferControlOpcode.Offer;
        evilBody.CopyTo(framedEvilOffer.AsSpan(1));

        ChannelId directChannelId = ChannelId.DeterministicForDirect(alice.IdentityId, bob.IdentityId, relay.Endpoint);
        using (var scope = alice.Services.CreateScope())
        {
            var sender = scope.ServiceProvider.GetRequiredService<IPayloadSender>();
            var sendResult = await sender.SendPayloadAsync(new OutboundPayloadContext(
                directChannelId,
                alice.IdentityId,
                bob.IdentityId,
                DeviceId.Primary,
                AppId.FileTransferControl,
                framedEvilOffer,
                PluginDeliveryRoute.RelayedOneToOne)).ConfigureAwait(false);

            sendResult.IsSuccess.Should().BeTrue();
        }

        // Send a normal chat message right after so we deterministically know Bob has drained the queue past the evil offer
        await alice.SendChatAsync(directChannelId, "post-evil-sentinel").ConfigureAwait(false);
        await bob.WaitForMessageAsync(directChannelId, "post-evil-sentinel", TimeSpan.FromSeconds(10)).ConfigureAwait(false);

        // Verify Bob rejected/discarded the malicious offer completely (not stored in DB, zero files created)
        using (var scope = bob.Services.CreateScope())
        {
            var repo = scope.ServiceProvider.GetRequiredService<ITransferSessionRepository>();
            var stagedEvil = await repo.GetSessionByIdAsync(evilSessionId).ConfigureAwait(false);
            stagedEvil.Should().BeNull("path traversal offer '../escape.txt' must be rejected during ingress deserialization");
        }

        File.Exists(Path.Combine(_testTempRoot, "escape.txt")).Should().BeFalse();
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------
    private async Task<(TestPersonaNode Alice, TestPersonaNode Bob, TestPersonaNode Charlie, ChannelId GroupChannelId)> SetupThreeNodeGroupAsync(
        TestRelayServer relay,
        string groupName)
    {
        var alice = await _topology.CreatePersonaAsync("Alice").ConfigureAwait(false);
        var bob = await _topology.CreatePersonaAsync("Bob").ConfigureAwait(false);
        var charlie = await _topology.CreatePersonaAsync("Charlie").ConfigureAwait(false);

        alice.EnableFileTransfers();
        bob.EnableFileTransfers();
        charlie.EnableFileTransfers();

        await alice.LinkRelayAsync(relay).ConfigureAwait(false);
        await bob.LinkRelayAsync(relay).ConfigureAwait(false);
        await charlie.LinkRelayAsync(relay).ConfigureAwait(false);

        await _topology.PairPersonasAsync(bob, alice, relay).ConfigureAwait(false);
        await _topology.PairPersonasAsync(charlie, alice, relay).ConfigureAwait(false);
        await _topology.PairPersonasAsync(charlie, bob, relay).ConfigureAwait(false);

        var allPersonas = new List<TestPersonaNode> { alice, bob, charlie };
        var allMemberIds = allPersonas.Select(p => p.IdentityId).ToList();

        ChannelId groupChannelId = await alice.CreateGroupAsync(relay, groupName, allPersonas).ConfigureAwait(false);
        await bob.WaitForGroupReceiverSessionAsync(groupChannelId, alice.IdentityId, TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        await charlie.WaitForGroupReceiverSessionAsync(groupChannelId, alice.IdentityId, TimeSpan.FromSeconds(10)).ConfigureAwait(false);

        await bob.JoinGroupAsync(relay, groupChannelId, groupName, alice.IdentityId, allMemberIds).ConfigureAwait(false);
        await charlie.JoinGroupAsync(relay, groupChannelId, groupName, alice.IdentityId, allMemberIds).ConfigureAwait(false);

        await alice.WaitForGroupReceiverSessionAsync(groupChannelId, bob.IdentityId, TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        await alice.WaitForGroupReceiverSessionAsync(groupChannelId, charlie.IdentityId, TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        await bob.WaitForGroupReceiverSessionAsync(groupChannelId, charlie.IdentityId, TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        await charlie.WaitForGroupReceiverSessionAsync(groupChannelId, bob.IdentityId, TimeSpan.FromSeconds(10)).ConfigureAwait(false);

        return (alice, bob, charlie, groupChannelId);
    }

    private string CreateSubDir(string name)
    {
        string path = Path.Combine(_testTempRoot, name);
        Directory.CreateDirectory(path);
        return path;
    }

    private static byte[] WriteDeterministicFile(string rootDir, string relativePath, int sizeBytes, int seed)
    {
        string fullPath = Path.Combine(rootDir, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);

        byte[] data = new byte[sizeBytes];
        new Random(seed).NextBytes(data);
        File.WriteAllBytes(fullPath, data);
        return SHA256.HashData(data);
    }

    private static byte[] ComputeFileSha256(string fullPath)
    {
        File.Exists(fullPath).Should().BeTrue($"expected file '{fullPath}' to exist on disk");
        using var fs = File.OpenRead(fullPath);
        return SHA256.HashData(fs);
    }

    private static void AssertZeroDiskFiles(string directory)
    {
        if (!Directory.Exists(directory))
        {
            return;
        }

        Directory.GetFiles(directory, "*", SearchOption.AllDirectories)
            .Should()
            .BeEmpty("Exchange must write 0 bytes to disk for SwarmTracker / TransferPipe traffic");
    }

    private static async Task<TransferSession> WaitForSessionInDbAsync(
        TestPersonaNode node,
        TransferSessionId sessionId,
        TransferSessionState expectedState,
        TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(50));
        while (!cts.IsCancellationRequested)
        {
            using var scope = node.Services.CreateScope();
            var repo = scope.ServiceProvider.GetRequiredService<ITransferSessionRepository>();
            TransferSession? session = await repo.GetSessionByIdAsync(sessionId, cts.Token).ConfigureAwait(false);
            if (session != null && session.State == expectedState)
            {
                return session;
            }

            try
            {
                if (!await timer.WaitForNextTickAsync(cts.Token).ConfigureAwait(false)) break;
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        throw new TimeoutException($"Timed out waiting for session {sessionId.Value} on {node.DisplayName} to reach DB state {expectedState}.");
    }

    private static async Task<TransferSession> WaitForRuntimeStateAsync(
        TestPersonaNode node,
        TransferSessionId sessionId,
        TransferSessionState expectedState,
        TimeSpan timeout)
    {
        var runtime = node.Services.GetRequiredService<ITransferSessionRuntime>();
        using var cts = new CancellationTokenSource(timeout);
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(50));
        while (!cts.IsCancellationRequested)
        {
            TransferSession? session = runtime.GetActiveSession(sessionId);
            if (session != null && session.State == expectedState)
            {
                return session;
            }

            try
            {
                if (!await timer.WaitForNextTickAsync(cts.Token).ConfigureAwait(false)) break;
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        TransferSession? last = runtime.GetActiveSession(sessionId);
        throw new TimeoutException(
            $"Timed out waiting for session {sessionId.Value} on {node.DisplayName} to reach runtime state {expectedState} (last={last?.State.ToString() ?? "null"}, chunks={last?.LocalBitfield.PopCount ?? 0}/{last?.Manifest.TotalChunks.Count ?? 0}).");
    }

    private static async Task WaitForConditionAsync(Func<bool> predicate, TimeSpan timeout, string failureMessage)
    {
        using var cts = new CancellationTokenSource(timeout);
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(50));
        while (!cts.IsCancellationRequested)
        {
            if (predicate())
            {
                return;
            }

            try
            {
                if (!await timer.WaitForNextTickAsync(cts.Token).ConfigureAwait(false)) break;
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        predicate().Should().BeTrue(failureMessage);
    }

    private sealed class RecordingTransferStream : ITransferStream
    {
        public StreamIndex Index => StreamIndex.FromInt(0);
        public bool WasDisposed { get; private set; }

        public ValueTask SendFrameAsync(IStreamFramePayload frame, CancellationToken ct = default) =>
            ValueTask.CompletedTask;

        public ValueTask<IStreamFramePayload> ReceiveFrameAsync(CancellationToken ct = default) =>
            throw new NotSupportedException();

        public ValueTask DisposeAsync()
        {
            WasDisposed = true;
            return ValueTask.CompletedTask;
        }
    }
}

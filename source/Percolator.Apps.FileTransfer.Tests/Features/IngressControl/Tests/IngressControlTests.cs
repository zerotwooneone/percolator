using Percolator.Apps.FileTransfer.Domain.Common;
using Percolator.Apps.FileTransfer.Domain.ManifestManagement;
using Percolator.Apps.FileTransfer.Domain.TransferNegotiation;
using Percolator.Apps.FileTransfer.Features.IngressControl;
using Percolator.Apps.FileTransfer.Features.TransferNegotiation;
using Percolator.Apps.FileTransfer.Tests.TestDoubles;
using Percolator.Domain.Channels.ValueObjects;
using Percolator.Domain.Common;
using Percolator.Domain.Identities.ValueObjects;
using Percolator.Infrastructure2.Serialization;
using Percolator.PluginSdk;

namespace Percolator.Apps.FileTransfer.Tests.Features.IngressControl.Tests;

[TestFixture]
public sealed class IngressControlTests
{
    private FakeDateTimeProvider _clock = null!;
    private InMemoryTransferSessionRepository _sessionRepository = null!;
    private InMemoryFilterPolicyRepository _policyRepository = null!;
    private IPayloadSerializer _serializer = null!;

    [SetUp]
    public void SetUp()
    {
        _clock = new FakeDateTimeProvider();
        _sessionRepository = new InMemoryTransferSessionRepository();
        _policyRepository = new InMemoryFilterPolicyRepository();
        _serializer = new ProtobufPayloadSerializer();
    }

    [Test]
    public void UT_ING_01_EdgeFilterSilentDiscardWhenPluginDisabled()
    {
        FileTransferIngressFilter filter = new(_policyRepository)
        {
            IsEnabled = false
        };

        InboundPayloadContext context = new(
            ChannelId.New(),
            PublicIdentityId.New(),
            DeviceId.Primary,
            AppId.FileTransferControl,
            new byte[] { (byte)FileTransferControlOpcode.Offer },
            DateTimeOffset.UtcNow);

        IngressDecision decision = filter.Evaluate(context);

        decision.Should().Be(IngressDecision.DiscardSilently);
    }

    [Test]
    public void UT_ING_02_IngressPeerBlockingPolicyEnforcement()
    {
        PublicIdentityId blockedPeer = PublicIdentityId.New();
        _policyRepository.Policy = FileTransferFilterPolicy.CreateDefault().BlockPeer(blockedPeer);

        FileTransferIngressFilter filter = new(_policyRepository)
        {
            IsEnabled = true
        };

        InboundPayloadContext context = new(
            ChannelId.New(),
            blockedPeer,
            DeviceId.Primary,
            AppId.FileTransferControl,
            new byte[] { (byte)FileTransferControlOpcode.Offer },
            DateTimeOffset.UtcNow);

        IngressDecision decision = filter.Evaluate(context);

        decision.Should().Be(IngressDecision.DiscardSilently);
    }

    [Test]
    public async Task UT_ING_03_IngressRelayUriBlockingPolicyEnforcement()
    {
        RelayPipeEndpointUri blockedRelay = RelayPipeEndpointUri.Parse("https://malicious-relay.com/pipe");
        _policyRepository.Policy = FileTransferFilterPolicy.CreateDefault().BlockRelay(blockedRelay);

        FileTransferPayloadHandler handler = new(_sessionRepository, _policyRepository, _serializer, _clock);

        TransferSessionId sessionId = TransferSessionId.New();
        FileTransferOfferPayload offer = CreateValidOffer(sessionId, relayUri: blockedRelay);
        byte[] payloadBytes = PrependOpcode((byte)FileTransferControlOpcode.Offer, _serializer.Serialize(offer).Span);

        PublicIdentityId senderId = PublicIdentityId.New();
        InboundPayloadContext context = new(
            ChannelId.FromGuid(sessionId.Value),
            senderId,
            DeviceId.Primary,
            AppId.FileTransferControl,
            payloadBytes,
            DateTimeOffset.UtcNow);

        DomainResult result = await handler.HandleInboundAsync(context);

        result.IsSuccess.Should().BeTrue();
        IReadOnlyList<TransferSession> active = await _sessionRepository.GetActiveSessionsAsync();
        active.Should().BeEmpty();
    }

    [Test]
    public async Task UT_ING_04_IngressOfferStagingForAllowedPeer()
    {
        FileTransferPayloadHandler handler = new(_sessionRepository, _policyRepository, _serializer, _clock);

        TransferSessionId sessionId = TransferSessionId.New();
        FileTransferOfferPayload offer = CreateValidOffer(sessionId);
        byte[] payloadBytes = PrependOpcode((byte)FileTransferControlOpcode.Offer, _serializer.Serialize(offer).Span);

        PublicIdentityId senderId = PublicIdentityId.New();
        InboundPayloadContext context = new(
            ChannelId.FromGuid(sessionId.Value),
            senderId,
            DeviceId.Primary,
            AppId.FileTransferControl,
            payloadBytes,
            DateTimeOffset.UtcNow);

        DomainResult result = await handler.HandleInboundAsync(context);

        result.IsSuccess.Should().BeTrue();
        TransferSession? staged = await _sessionRepository.GetSessionByIdAsync(sessionId);
        staged.Should().NotBeNull();
        staged!.State.Should().Be(TransferSessionState.PendingConsent);
    }

    [Test]
    public void UT_ING_05_OfferRejectionOnBundleFileCountOverflow()
    {
        TransferSessionId sessionId = TransferSessionId.New();
        TransferBundleSummary summary = TransferBundleSummary.Create(
            RootBundleName.Parse("OverflowBundle"),
            TotalBundleBytes.FromBytes(1000),
            FileCount.FromInt(50_001), // Cap is 50,000
            ChunkSizeBytes.Default,
            ChunkCount.FromInt(1),
            MerkleRoot.FromBytes(new byte[32]));

        FileTransferOfferPayload offer = new(
            sessionId,
            summary,
            ManifestDistribution.FromInline(InlineManifestData.FromBytes(new byte[10])),
            TransferKey.Generate(),
            BaseNonce.Generate(),
            StreamAuthToken.Generate(),
            RelayRendezvous: null,
            DirectCandidates: []);

        ValidationResult validation = IngressTransferOfferValidator.ValidateOffer(offer);
        validation.IsValid.Should().BeFalse();
    }

    [Test]
    public void UT_ING_06_OfferRejectionOnBundleChunkCountOverflow()
    {
        TransferSessionId sessionId = TransferSessionId.New();
        TransferBundleSummary summary = TransferBundleSummary.Create(
            RootBundleName.Parse("OverflowChunks"),
            TotalBundleBytes.FromBytes(1000),
            FileCount.FromInt(1),
            ChunkSizeBytes.Default,
            ChunkCount.FromInt(1_000_001), // Cap is 1,000,000
            MerkleRoot.FromBytes(new byte[32]));

        FileTransferOfferPayload offer = new(
            sessionId,
            summary,
            ManifestDistribution.FromInline(InlineManifestData.FromBytes(new byte[10])),
            TransferKey.Generate(),
            BaseNonce.Generate(),
            StreamAuthToken.Generate(),
            RelayRendezvous: null,
            DirectCandidates: []);

        ValidationResult validation = IngressTransferOfferValidator.ValidateOffer(offer);
        validation.IsValid.Should().BeFalse();
    }

    [Test]
    public void UT_ING_07_OfferRejectionOnBundleByteSizeOverflow()
    {
        TransferSessionId sessionId = TransferSessionId.New();
        TransferBundleSummary summary = TransferBundleSummary.Create(
            RootBundleName.Parse("OverflowBytes"),
            TotalBundleBytes.FromBytes(16_000_000_000_001L), // Cap is 16 TB
            FileCount.FromInt(1),
            ChunkSizeBytes.Default,
            ChunkCount.FromInt(1),
            MerkleRoot.FromBytes(new byte[32]));

        FileTransferOfferPayload offer = new(
            sessionId,
            summary,
            ManifestDistribution.FromInline(InlineManifestData.FromBytes(new byte[10])),
            TransferKey.Generate(),
            BaseNonce.Generate(),
            StreamAuthToken.Generate(),
            RelayRendezvous: null,
            DirectCandidates: []);

        ValidationResult validation = IngressTransferOfferValidator.ValidateOffer(offer);
        validation.IsValid.Should().BeFalse();
    }

    [Test]
    public void UT_ING_08_InBandManifestEnvelopeOverflowRejection()
    {
        byte[] oversizedBytes = new byte[49_153]; // Cap is 48 KB (49,152 bytes)
        Action act = () => InlineManifestData.FromBytes(oversizedBytes);
        act.Should().Throw<ArgumentException>();
    }

    [Test]
    public void UT_ING_09_RemoteManifestBlobSizeBoundsVerification()
    {
        TransferSessionId sessionId = TransferSessionId.New();
        TransferBundleSummary summary = TransferBundleSummary.Create(
            RootBundleName.Parse("RemoteBlobOverflow"),
            TotalBundleBytes.FromBytes(10_000_000),
            FileCount.FromInt(1),
            ChunkSizeBytes.Default,
            ChunkCount.FromInt(1),
            MerkleRoot.FromBytes(new byte[32]));

        ManifestBlobDescriptor blob = new(
            new BlobId("abcdef123456"),
            ChunkHash.FromBytes(new byte[32]),
            TotalBundleBytes.FromBytes(8_388_609L), // Cap is 8 MB (8,388,608 bytes)
            RelayPipeEndpointUri.Parse("https://relay.example.com/pipe"));

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
    public async Task UT_ING_10_InboundCancellationFromAuthorizedInitiator()
    {
        FileTransferPayloadHandler handler = new(_sessionRepository, _policyRepository, _serializer, _clock);

        TransferSessionId sessionId = TransferSessionId.New();
        PublicIdentityId initiatorId = PublicIdentityId.New();

        TransferManifest manifest = TransferManifest.Create(
            sessionId,
            RootBundleName.Parse("CancelTest"),
            ChunkSizeBytes.Default,
            MerkleTreeBuilder.BuildFromLeaves([ChunkHash.FromBytes(new byte[32])]),
            [new FileEntry(FileIndex.FromInt(0), SanitizedRelativePath.Parse("test.bin"), FileSizeBytes.FromBytes(100), BundleByteOffset.Zero, FileLastModifiedUtc.FromDateTimeOffset(DateTimeOffset.UtcNow))]);

        TransferSession session = TransferSession.CreateIncomingStaged(sessionId, initiatorId, manifest, SeedRatio.Zero, _clock);
        await _sessionRepository.SaveSessionAsync(session);

        FileTransferCancelPayload cancel = new(sessionId, TransferTerminationReason.SenderCancelled);
        byte[] payloadBytes = PrependOpcode((byte)FileTransferControlOpcode.Cancel, _serializer.Serialize(cancel).Span);

        InboundPayloadContext context = new(
            ChannelId.FromGuid(sessionId.Value),
            initiatorId,
            DeviceId.Primary,
            AppId.FileTransferControl,
            payloadBytes,
            DateTimeOffset.UtcNow);

        DomainResult result = await handler.HandleInboundAsync(context);

        result.IsSuccess.Should().BeTrue();
        TransferSession? updated = await _sessionRepository.GetSessionByIdAsync(sessionId);
        updated!.State.Should().Be(TransferSessionState.Cancelled);
    }

    [Test]
    public async Task UT_ING_11_InboundCancellationFromUnauthorizedThirdParty()
    {
        FileTransferPayloadHandler handler = new(_sessionRepository, _policyRepository, _serializer, _clock);

        TransferSessionId sessionId = TransferSessionId.New();
        byte[] idBytes1 = new byte[16];
        idBytes1[0] = 1;
        PublicIdentityId initiatorId = PublicIdentityId.FromBytes(idBytes1);

        TransferManifest manifest = TransferManifest.Create(
            sessionId,
            RootBundleName.Parse("UnauthorizedCancelTest"),
            ChunkSizeBytes.Default,
            MerkleTreeBuilder.BuildFromLeaves([ChunkHash.FromBytes(new byte[32])]),
            [new FileEntry(FileIndex.FromInt(0), SanitizedRelativePath.Parse("test.bin"), FileSizeBytes.FromBytes(100), BundleByteOffset.Zero, FileLastModifiedUtc.FromDateTimeOffset(DateTimeOffset.UtcNow))]);

        TransferSession session = TransferSession.CreateIncomingStaged(sessionId, initiatorId, manifest, SeedRatio.Zero, _clock);
        await _sessionRepository.SaveSessionAsync(session);

        byte[] attackerBytes = new byte[16];
        attackerBytes[0] = 99;
        PublicIdentityId attackerId = PublicIdentityId.FromBytes(attackerBytes);

        FileTransferCancelPayload cancel = new(sessionId, TransferTerminationReason.SenderCancelled);
        byte[] payloadBytes = PrependOpcode((byte)FileTransferControlOpcode.Cancel, _serializer.Serialize(cancel).Span);

        InboundPayloadContext context = new(
            ChannelId.FromGuid(sessionId.Value),
            attackerId,
            DeviceId.Primary,
            AppId.FileTransferControl,
            payloadBytes,
            DateTimeOffset.UtcNow);

        DomainResult result = await handler.HandleInboundAsync(context);

        result.IsSuccess.Should().BeTrue();
        TransferSession? unchanged = await _sessionRepository.GetSessionByIdAsync(sessionId);
        unchanged!.State.Should().Be(TransferSessionState.PendingConsent);
    }

    private FileTransferOfferPayload CreateValidOffer(TransferSessionId sessionId, RelayPipeEndpointUri? relayUri = null)
    {
        TransferManifest manifest = TransferManifest.Create(
            sessionId,
            RootBundleName.Parse("ValidBundle"),
            ChunkSizeBytes.Default,
            MerkleTreeBuilder.BuildFromLeaves([ChunkHash.FromBytes(new byte[32])]),
            [new FileEntry(FileIndex.FromInt(0), SanitizedRelativePath.Parse("file.bin"), FileSizeBytes.FromBytes(1024), BundleByteOffset.Zero, FileLastModifiedUtc.FromDateTimeOffset(DateTimeOffset.UtcNow))]);

        ReadOnlyMemory<byte> manifestBytes = _serializer.Serialize(manifest);

        TransferBundleSummary summary = TransferBundleSummary.Create(
            manifest.RootName,
            manifest.TotalSize,
            manifest.TotalFiles,
            ChunkSizeBytes.Default,
            manifest.TotalChunks,
            manifest.MerkleTree.Root);

        RelayRendezvousDescriptor? relay = relayUri != null
            ? new RelayRendezvousDescriptor(relayUri, RelayPipeToken.FromBytes(new byte[32]))
            : null;

        return new FileTransferOfferPayload(
            sessionId,
            summary,
            ManifestDistribution.FromInline(InlineManifestData.FromSpan(manifestBytes.Span)),
            TransferKey.Generate(),
            BaseNonce.Generate(),
            StreamAuthToken.Generate(),
            relay,
            DirectCandidates: []);
    }

    private static byte[] PrependOpcode(byte opcode, ReadOnlySpan<byte> payload)
    {
        byte[] combined = new byte[1 + payload.Length];
        combined[0] = opcode;
        payload.CopyTo(combined.AsSpan(1));
        return combined;
    }
}

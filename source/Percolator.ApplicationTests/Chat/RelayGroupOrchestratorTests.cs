using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using Percolator.Application.Chat;
using Percolator.Chat.GroupLedger;
using Percolator.Chat.Messaging.ValueObjects;
using Percolator.Cryptography;
using Percolator.Identity;
using Percolator.Identity.Model;
using ChatPeerId = Percolator.Chat.GroupMembership.ChatPeerId;
using PublicIdentityId = Percolator.Identity.PublicIdentityId;

namespace Percolator.ApplicationTests.Chat;

[TestFixture]
public class RelayGroupOrchestratorTests
{
    private class FakeGroupCryptographyService : IGroupCryptographyService
    {
        public bool ShouldVerifySucceed { get; set; } = true;

        public GroupMasterKey GenerateGroupMasterKey(ReadOnlySpan<byte> randomness32)
        {
            throw new NotImplementedException();
        }

        public GroupId DeriveGroupId(GroupMasterKey masterKey)
        {
            throw new NotImplementedException();
        }

        public BlobKey DeriveBlobKey(GroupMasterKey masterKey)
        {
            throw new NotImplementedException();
        }

        public byte[] SerializeGroupMasterKey(GroupMasterKey masterKey)
        {
            throw new NotImplementedException();
        }

        public void SerializeGroupMasterKey(GroupMasterKey masterKey, Span<byte> buffer32)
        {
            throw new NotImplementedException();
        }

        public GroupMasterKey DeserializeGroupMasterKey(ReadOnlySpan<byte> bytes32)
        {
            throw new NotImplementedException();
        }

        public ZkGroupPublicParamsBytes DeriveGroupPublicParams(GroupMasterKey masterKey)
        {
            throw new NotImplementedException();
        }

        public bool VerifyGroupPresentation(
            ZkPresentationBytes presentation,
            ZkServerSecretParamsSeedBytes serverSecretSeed,
            ZkGroupPublicParamsBytes groupPublicParams,
            ulong redemptionTimeEpochSeconds)
        {
            return ShouldVerifySucceed;
        }

        public byte[] EncryptGroupProfile(GroupMasterKey masterKey, ProfilePlaintextBytes profilePlaintext)
        {
            throw new NotImplementedException();
        }

        public ProfilePlaintextBytes DecryptGroupProfile(GroupMasterKey masterKey, ReadOnlySpan<byte> ciphertext)
        {
            throw new NotImplementedException();
        }
    }

    private class FakeTimeProvider : TimeProvider
    {
        public override DateTimeOffset GetUtcNow()
        {
            return new DateTimeOffset(2025, 1, 1, 12, 0, 0, TimeSpan.Zero);
        }
    }

    private static RelayGroupOrchestrator CreateOrchestrator(
        Mock<ISelfIdentityQueries> identityQueries,
        FakeGroupCryptographyService cryptoService,
        Mock<IRelayGroupLedgerRepository> ledgerRepository,
        Mock<IRelayRosterQueries> rosterQueries,
        Mock<IRelayMessagePublisher> publisher,
        Mock<IPeerIdentityRepository> peerIdentityRepository)
    {
        var logger = Mock.Of<ILogger<RelayGroupOrchestrator>>();
        var timeProvider = new FakeTimeProvider();
        return new RelayGroupOrchestrator(
            identityQueries.Object,
            cryptoService,
            ledgerRepository.Object,
            rosterQueries.Object,
            publisher.Object,
            peerIdentityRepository.Object,
            timeProvider,
            logger);
    }

    [Test]
    public async Task PublishGroupRelayMessageAsync_WhenVerificationFails_ReturnsUnauthorized()
    {
        // ARRANGE
        var identityQueries = new Mock<ISelfIdentityQueries>();
        var cryptoService = new FakeGroupCryptographyService { ShouldVerifySucceed = false };
        var ledgerRepository = new Mock<IRelayGroupLedgerRepository>();
        var rosterQueries = new Mock<IRelayRosterQueries>();
        var publisher = new Mock<IRelayMessagePublisher>();
        var peerIdentityRepository = new Mock<IPeerIdentityRepository>();

        var conversationId = new ConversationId(Guid.NewGuid());
        var requestedEpoch = 1U;
        var presentation = ZkPresentationBytes.FromBytesOwned(new byte[] { 0x01, 0x02 });
        var ciphertext = CiphertextBytes.FromBytesOwned(new byte[] { 0x03, 0x04 });
        var seed = ZkServerSecretParamsSeedBytes.FromBytesOwned(new byte[32]);
        var encryptedProfile = EncryptedGroupProfileBytes.FromBytesOwned(new byte[] { 0x05, 0x06 });
        var ledger = new RelayGroupLedger(conversationId, 0, RelayGroupPublicParamsBytes.FromBytesOwned(new byte[32]), encryptedProfile, 1);

        identityQueries.Setup(q => q.GetZkServerSecretParamsSeedAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(seed);
        ledgerRepository.Setup(r => r.GetByIdAsync(conversationId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(ledger);

        var orchestrator = CreateOrchestrator(identityQueries, cryptoService, ledgerRepository, rosterQueries, publisher, peerIdentityRepository);

        // ACT
        var result = await orchestrator.PublishGroupRelayMessageAsync(
            conversationId, requestedEpoch, presentation, ciphertext, CancellationToken.None);

        // ASSERT
        result.Should().Be(RelayGroupOperationStatus.Unauthorized);
    }

    [Test]
    public async Task PublishGroupRelayMessageAsync_WhenEpochMismatch_ReturnsEpochConflict()
    {
        // ARRANGE
        var identityQueries = new Mock<ISelfIdentityQueries>();
        var cryptoService = new FakeGroupCryptographyService { ShouldVerifySucceed = true };
        var ledgerRepository = new Mock<IRelayGroupLedgerRepository>();
        var rosterQueries = new Mock<IRelayRosterQueries>();
        var publisher = new Mock<IRelayMessagePublisher>();
        var peerIdentityRepository = new Mock<IPeerIdentityRepository>();

        var conversationId = new ConversationId(Guid.NewGuid());
        var requestedEpoch = 5U; // Stale epoch
        var presentation = ZkPresentationBytes.FromBytesOwned(new byte[] { 0x01, 0x02 });
        var ciphertext = CiphertextBytes.FromBytesOwned(new byte[] { 0x03, 0x04 });
        var seed = ZkServerSecretParamsSeedBytes.FromBytesOwned(new byte[32]);
        var encryptedProfile = EncryptedGroupProfileBytes.FromBytesOwned(new byte[] { 0x05, 0x06 });
        var ledger = new RelayGroupLedger(conversationId, 10, RelayGroupPublicParamsBytes.FromBytesOwned(new byte[32]), encryptedProfile, 1);

        identityQueries.Setup(q => q.GetZkServerSecretParamsSeedAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(seed);
        ledgerRepository.Setup(r => r.GetByIdAsync(conversationId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(ledger);

        var orchestrator = CreateOrchestrator(identityQueries, cryptoService, ledgerRepository, rosterQueries, publisher, peerIdentityRepository);

        // ACT
        var result = await orchestrator.PublishGroupRelayMessageAsync(
            conversationId, requestedEpoch, presentation, ciphertext, CancellationToken.None);

        // ASSERT
        result.Should().Be(RelayGroupOperationStatus.EpochConflict);
    }

    [Test]
    public async Task PublishGroupRelayMessageAsync_WhenValid_ReturnsSuccess()
    {
        // ARRANGE
        var identityQueries = new Mock<ISelfIdentityQueries>();
        var cryptoService = new FakeGroupCryptographyService { ShouldVerifySucceed = true };
        var ledgerRepository = new Mock<IRelayGroupLedgerRepository>();
        var rosterQueries = new Mock<IRelayRosterQueries>();
        var publisher = new Mock<IRelayMessagePublisher>();
        var peerIdentityRepository = new Mock<IPeerIdentityRepository>();

        var conversationId = new ConversationId(Guid.NewGuid());
        var requestedEpoch = 10U;
        var presentation = ZkPresentationBytes.FromBytesOwned(new byte[] { 0x01, 0x02 });
        var ciphertext = CiphertextBytes.FromBytesOwned(new byte[] { 0x03, 0x04 });
        var seed = ZkServerSecretParamsSeedBytes.FromBytesOwned(new byte[32]);
        var encryptedProfile = EncryptedGroupProfileBytes.FromBytesOwned(new byte[] { 0x05, 0x06 });
        var ledger = new RelayGroupLedger(conversationId, 10, RelayGroupPublicParamsBytes.FromBytesOwned(new byte[32]), encryptedProfile, 1);
        var peerIds = new List<ChatPeerId> { new ChatPeerId(1), new ChatPeerId(2) };

        identityQueries.Setup(q => q.GetZkServerSecretParamsSeedAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(seed);
        ledgerRepository.Setup(r => r.GetByIdAsync(conversationId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(ledger);
        rosterQueries.Setup(r => r.GetMemberPeerIdsAsync(new ConversationId(conversationId.Value), It.IsAny<CancellationToken>()))
            .ReturnsAsync(peerIds);

        var orchestrator = CreateOrchestrator(identityQueries, cryptoService, ledgerRepository, rosterQueries, publisher, peerIdentityRepository);

        // ACT
        var result = await orchestrator.PublishGroupRelayMessageAsync(
            conversationId, requestedEpoch, presentation, ciphertext, CancellationToken.None);

        // ASSERT
        result.Should().Be(RelayGroupOperationStatus.Success);
    }

    [Test]
    public async Task ModifyGroupAsync_WhenValid_AppliesMutationAndUpdatesRepository()
    {
        // ARRANGE
        var identityQueries = new Mock<ISelfIdentityQueries>();
        var cryptoService = new FakeGroupCryptographyService { ShouldVerifySucceed = true };
        var ledgerRepository = new Mock<IRelayGroupLedgerRepository>();
        var rosterQueries = new Mock<IRelayRosterQueries>();
        var publisher = new Mock<IRelayMessagePublisher>();
        var peerIdentityRepository = new Mock<IPeerIdentityRepository>();

        var conversationId = new ConversationId(Guid.NewGuid());
        var baseEpoch = 5U;
        var presentation = ZkPresentationBytes.FromBytesOwned(new byte[] { 0x01, 0x02 });
        var newEncryptedProfile = EncryptedGroupProfileBytes.FromBytesOwned(new byte[] { 0x05, 0x06 });
        var addPublicIdentityId = new PublicIdentityId(Guid.NewGuid());
        var removePublicIdentityId = new PublicIdentityId(Guid.NewGuid());
        var seed = ZkServerSecretParamsSeedBytes.FromBytesOwned(new byte[32]);
        var encryptedProfile = EncryptedGroupProfileBytes.FromBytesOwned(new byte[] { 0x07, 0x08 });
        var ledger = new RelayGroupLedger(conversationId, 5, RelayGroupPublicParamsBytes.FromBytesOwned(new byte[32]), encryptedProfile, 1);
        var addPeerId = new PeerId(100);
        var removePeerId = new PeerId(200);

        identityQueries.Setup(q => q.GetZkServerSecretParamsSeedAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(seed);
        ledgerRepository.Setup(r => r.GetByIdAsync(conversationId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(ledger);
        peerIdentityRepository.Setup(r => r.GetOrCreateAsync(addPublicIdentityId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PeerIdentity(addPeerId, addPublicIdentityId));
        peerIdentityRepository.Setup(r => r.GetOrCreateAsync(removePublicIdentityId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PeerIdentity(removePeerId, removePublicIdentityId));
        ledgerRepository.Setup(r => r.UpdateGroupStateAsync(
            It.IsAny<RelayGroupLedger>(),
            It.IsAny<IReadOnlyList<ChatPeerId>>(),
            It.IsAny<IReadOnlyList<ChatPeerId>>(),
            It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var orchestrator = CreateOrchestrator(identityQueries, cryptoService, ledgerRepository, rosterQueries, publisher, peerIdentityRepository);

        // ACT
        var result = await orchestrator.ModifyGroupAsync(
            conversationId,
            baseEpoch,
            presentation,
            newEncryptedProfile,
            new List<PublicIdentityId> { addPublicIdentityId },
            new List<PublicIdentityId> { removePublicIdentityId },
            CancellationToken.None);

        // ASSERT
        result.Should().Be(RelayGroupOperationStatus.Success);
        ledger.CurrentEpoch.Should().Be(6); // Epoch advanced
        ledger.EncryptedProfile.Should().Be(newEncryptedProfile);
    }

    [Test]
    public async Task ModifyGroupAsync_WhenEpochConflict_ReturnsEpochConflict()
    {
        // ARRANGE
        var identityQueries = new Mock<ISelfIdentityQueries>();
        var cryptoService = new FakeGroupCryptographyService { ShouldVerifySucceed = true };
        var ledgerRepository = new Mock<IRelayGroupLedgerRepository>();
        var rosterQueries = new Mock<IRelayRosterQueries>();
        var publisher = new Mock<IRelayMessagePublisher>();
        var peerIdentityRepository = new Mock<IPeerIdentityRepository>();

        var conversationId = new ConversationId(Guid.NewGuid());
        var baseEpoch = 3U; // Stale epoch
        var presentation = ZkPresentationBytes.FromBytesOwned(new byte[] { 0x01, 0x02 });
        var newEncryptedProfile = EncryptedGroupProfileBytes.FromBytesOwned(new byte[] { 0x05, 0x06 });
        var seed = ZkServerSecretParamsSeedBytes.FromBytesOwned(new byte[32]);
        var encryptedProfile = EncryptedGroupProfileBytes.FromBytesOwned(new byte[] { 0x07, 0x08 });
        var ledger = new RelayGroupLedger(conversationId, 5, RelayGroupPublicParamsBytes.FromBytesOwned(new byte[32]), encryptedProfile, 1);

        identityQueries.Setup(q => q.GetZkServerSecretParamsSeedAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(seed);
        ledgerRepository.Setup(r => r.GetByIdAsync(conversationId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(ledger);

        var orchestrator = CreateOrchestrator(identityQueries, cryptoService, ledgerRepository, rosterQueries, publisher, peerIdentityRepository);

        // ACT
        var result = await orchestrator.ModifyGroupAsync(
            conversationId,
            baseEpoch,
            presentation,
            newEncryptedProfile,
            new List<PublicIdentityId>(),
            new List<PublicIdentityId>(),
            CancellationToken.None);

        // ASSERT
        result.Should().Be(RelayGroupOperationStatus.EpochConflict);
    }

    [Test]
    public async Task GetGroupStateAsync_WhenValid_ReturnsLedger()
    {
        // ARRANGE
        var identityQueries = new Mock<ISelfIdentityQueries>();
        var cryptoService = new FakeGroupCryptographyService { ShouldVerifySucceed = true };
        var ledgerRepository = new Mock<IRelayGroupLedgerRepository>();
        var rosterQueries = new Mock<IRelayRosterQueries>();
        var publisher = new Mock<IRelayMessagePublisher>();
        var peerIdentityRepository = new Mock<IPeerIdentityRepository>();

        var conversationId = new ConversationId(Guid.NewGuid());
        var presentation = ZkPresentationBytes.FromBytesOwned(new byte[] { 0x01, 0x02 });
        var seed = ZkServerSecretParamsSeedBytes.FromBytesOwned(new byte[32]);
        var encryptedProfile = EncryptedGroupProfileBytes.FromBytesOwned(new byte[] { 0x05, 0x06 });
        var ledger = new RelayGroupLedger(conversationId, 10, RelayGroupPublicParamsBytes.FromBytesOwned(new byte[32]), encryptedProfile, 1);

        identityQueries.Setup(q => q.GetZkServerSecretParamsSeedAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(seed);
        ledgerRepository.Setup(r => r.GetByIdAsync(conversationId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(ledger);

        var orchestrator = CreateOrchestrator(identityQueries, cryptoService, ledgerRepository, rosterQueries, publisher, peerIdentityRepository);

        // ACT
        var (status, returnedLedger) = await orchestrator.GetGroupStateAsync(
            conversationId, presentation, CancellationToken.None);

        // ASSERT
        status.Should().Be(RelayGroupOperationStatus.Success);
        returnedLedger.Should().NotBeNull();
        returnedLedger!.CurrentEpoch.Should().Be(10);
        returnedLedger.EncryptedProfile.Should().Be(encryptedProfile);
    }

    [Test]
    public async Task GetGroupStateAsync_WhenVerificationFails_ReturnsUnauthorized()
    {
        // ARRANGE
        var identityQueries = new Mock<ISelfIdentityQueries>();
        var cryptoService = new FakeGroupCryptographyService { ShouldVerifySucceed = false };
        var ledgerRepository = new Mock<IRelayGroupLedgerRepository>();
        var rosterQueries = new Mock<IRelayRosterQueries>();
        var publisher = new Mock<IRelayMessagePublisher>();
        var peerIdentityRepository = new Mock<IPeerIdentityRepository>();

        var conversationId = new ConversationId(Guid.NewGuid());
        var presentation = ZkPresentationBytes.FromBytesOwned(new byte[] { 0x01, 0x02 });
        var seed = ZkServerSecretParamsSeedBytes.FromBytesOwned(new byte[32]);
        var groupPublicParams = RelayGroupPublicParamsBytes.FromBytesOwned(new byte[32]);
        var encryptedProfile = EncryptedGroupProfileBytes.FromBytesOwned(new byte[32]);

        var ledger = new RelayGroupLedger(
            conversationId,
            10,
            groupPublicParams,
            encryptedProfile,
            1);

        identityQueries.Setup(q => q.GetZkServerSecretParamsSeedAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(seed);

        ledgerRepository.Setup(r => r.GetByIdAsync(conversationId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(ledger);

        var orchestrator = CreateOrchestrator(identityQueries, cryptoService, ledgerRepository, rosterQueries, publisher, peerIdentityRepository);

        // ACT
        var (status, returnedLedger) = await orchestrator.GetGroupStateAsync(
            conversationId, presentation, CancellationToken.None);

        // ASSERT
        status.Should().Be(RelayGroupOperationStatus.Unauthorized);
        returnedLedger.Should().BeNull();
    }

    [Test]
    public async Task PublishGroupRelayMessageAsync_WhenGroupNotFound_ReturnsGroupNotFound()
    {
        // ARRANGE
        var identityQueries = new Mock<ISelfIdentityQueries>();
        var cryptoService = new FakeGroupCryptographyService { ShouldVerifySucceed = true };
        var ledgerRepository = new Mock<IRelayGroupLedgerRepository>();
        var rosterQueries = new Mock<IRelayRosterQueries>();
        var publisher = new Mock<IRelayMessagePublisher>();
        var peerIdentityRepository = new Mock<IPeerIdentityRepository>();

        var conversationId = new ConversationId(Guid.NewGuid());
        var requestedEpoch = 1U;
        var presentation = ZkPresentationBytes.FromBytesOwned(new byte[] { 0x01, 0x02 });
        var ciphertext = CiphertextBytes.FromBytesOwned(new byte[] { 0x03, 0x04 });
        var seed = ZkServerSecretParamsSeedBytes.FromBytesOwned(new byte[32]);

        identityQueries.Setup(q => q.GetZkServerSecretParamsSeedAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(seed);
        ledgerRepository.Setup(r => r.GetByIdAsync(conversationId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((RelayGroupLedger?)null);

        var orchestrator = CreateOrchestrator(identityQueries, cryptoService, ledgerRepository, rosterQueries, publisher, peerIdentityRepository);

        // ACT
        var result = await orchestrator.PublishGroupRelayMessageAsync(
            conversationId, requestedEpoch, presentation, ciphertext, CancellationToken.None);

        // ASSERT
        result.Should().Be(RelayGroupOperationStatus.GroupNotFound);
    }

    [Test]
    public async Task ModifyGroupAsync_WhenVerificationFails_ReturnsUnauthorized()
    {
        // ARRANGE
        var identityQueries = new Mock<ISelfIdentityQueries>();
        var cryptoService = new FakeGroupCryptographyService { ShouldVerifySucceed = false };
        var ledgerRepository = new Mock<IRelayGroupLedgerRepository>();
        var rosterQueries = new Mock<IRelayRosterQueries>();
        var publisher = new Mock<IRelayMessagePublisher>();
        var peerIdentityRepository = new Mock<IPeerIdentityRepository>();

        var conversationId = new ConversationId(Guid.NewGuid());
        var baseEpoch = 5U;
        var presentation = ZkPresentationBytes.FromBytesOwned(new byte[] { 0x01, 0x02 });
        var newEncryptedProfile = EncryptedGroupProfileBytes.FromBytesOwned(new byte[] { 0x05, 0x06 });
        var seed = ZkServerSecretParamsSeedBytes.FromBytesOwned(new byte[32]);
        var encryptedProfile = EncryptedGroupProfileBytes.FromBytesOwned(new byte[] { 0x07, 0x08 });
        var ledger = new RelayGroupLedger(conversationId, 5, RelayGroupPublicParamsBytes.FromBytesOwned(new byte[32]), encryptedProfile, 1);

        identityQueries.Setup(q => q.GetZkServerSecretParamsSeedAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(seed);
        ledgerRepository.Setup(r => r.GetByIdAsync(conversationId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(ledger);

        var orchestrator = CreateOrchestrator(identityQueries, cryptoService, ledgerRepository, rosterQueries, publisher, peerIdentityRepository);

        // ACT
        var result = await orchestrator.ModifyGroupAsync(
            conversationId,
            baseEpoch,
            presentation,
            newEncryptedProfile,
            new List<PublicIdentityId>(),
            new List<PublicIdentityId>(),
            CancellationToken.None);

        // ASSERT
        result.Should().Be(RelayGroupOperationStatus.Unauthorized);
    }

    [Test]
    public async Task ModifyGroupAsync_WhenGroupNotFound_ReturnsGroupNotFound()
    {
        // ARRANGE
        var identityQueries = new Mock<ISelfIdentityQueries>();
        var cryptoService = new FakeGroupCryptographyService { ShouldVerifySucceed = true };
        var ledgerRepository = new Mock<IRelayGroupLedgerRepository>();
        var rosterQueries = new Mock<IRelayRosterQueries>();
        var publisher = new Mock<IRelayMessagePublisher>();
        var peerIdentityRepository = new Mock<IPeerIdentityRepository>();

        var conversationId = new ConversationId(Guid.NewGuid());
        var baseEpoch = 5U;
        var presentation = ZkPresentationBytes.FromBytesOwned(new byte[] { 0x01, 0x02 });
        var newEncryptedProfile = EncryptedGroupProfileBytes.FromBytesOwned(new byte[] { 0x05, 0x06 });
        var seed = ZkServerSecretParamsSeedBytes.FromBytesOwned(new byte[32]);

        identityQueries.Setup(q => q.GetZkServerSecretParamsSeedAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(seed);
        ledgerRepository.Setup(r => r.GetByIdAsync(conversationId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((RelayGroupLedger?)null);

        var orchestrator = CreateOrchestrator(identityQueries, cryptoService, ledgerRepository, rosterQueries, publisher, peerIdentityRepository);

        // ACT
        var result = await orchestrator.ModifyGroupAsync(
            conversationId,
            baseEpoch,
            presentation,
            newEncryptedProfile,
            new List<PublicIdentityId>(),
            new List<PublicIdentityId>(),
            CancellationToken.None);

        // ASSERT
        result.Should().Be(RelayGroupOperationStatus.GroupNotFound);
    }

    [Test]
    public async Task GetGroupStateAsync_WhenGroupNotFound_ReturnsGroupNotFound()
    {
        // ARRANGE
        var identityQueries = new Mock<ISelfIdentityQueries>();
        var cryptoService = new FakeGroupCryptographyService { ShouldVerifySucceed = true };
        var ledgerRepository = new Mock<IRelayGroupLedgerRepository>();
        var rosterQueries = new Mock<IRelayRosterQueries>();
        var publisher = new Mock<IRelayMessagePublisher>();
        var peerIdentityRepository = new Mock<IPeerIdentityRepository>();

        var conversationId = new ConversationId(Guid.NewGuid());
        var presentation = ZkPresentationBytes.FromBytesOwned(new byte[] { 0x01, 0x02 });
        var seed = ZkServerSecretParamsSeedBytes.FromBytesOwned(new byte[32]);

        identityQueries.Setup(q => q.GetZkServerSecretParamsSeedAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(seed);
        ledgerRepository.Setup(r => r.GetByIdAsync(conversationId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((RelayGroupLedger?)null);

        var orchestrator = CreateOrchestrator(identityQueries, cryptoService, ledgerRepository, rosterQueries, publisher, peerIdentityRepository);

        // ACT
        var (status, returnedLedger) = await orchestrator.GetGroupStateAsync(
            conversationId, presentation, CancellationToken.None);

        // ASSERT
        status.Should().Be(RelayGroupOperationStatus.GroupNotFound);
        returnedLedger.Should().BeNull();
    }

    [Test]
    public async Task ModifyGroupAsync_WhenValid_MapsPublicIdentityIdsToPeerIds()
    {
        // ARRANGE
        var identityQueries = new Mock<ISelfIdentityQueries>();
        var cryptoService = new FakeGroupCryptographyService { ShouldVerifySucceed = true };
        var ledgerRepository = new Mock<IRelayGroupLedgerRepository>();
        var rosterQueries = new Mock<IRelayRosterQueries>();
        var publisher = new Mock<IRelayMessagePublisher>();
        var peerIdentityRepository = new Mock<IPeerIdentityRepository>();

        var conversationId = new ConversationId(Guid.NewGuid());
        var baseEpoch = 5U;
        var presentation = ZkPresentationBytes.FromBytesOwned(new byte[] { 0x01, 0x02 });
        var newEncryptedProfile = EncryptedGroupProfileBytes.FromBytesOwned(new byte[] { 0x05, 0x06 });
        var addPublicIdentityId = new PublicIdentityId(Guid.NewGuid());
        var removePublicIdentityId = new PublicIdentityId(Guid.NewGuid());
        var seed = ZkServerSecretParamsSeedBytes.FromBytesOwned(new byte[32]);
        var encryptedProfile = EncryptedGroupProfileBytes.FromBytesOwned(new byte[] { 0x07, 0x08 });
        var ledger = new RelayGroupLedger(conversationId, 5, RelayGroupPublicParamsBytes.FromBytesOwned(new byte[32]), encryptedProfile, 1);
        var addPeerId = new Percolator.Identity.PeerId(100);
        var removePeerId = new Percolator.Identity.PeerId(200);

        identityQueries.Setup(q => q.GetZkServerSecretParamsSeedAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(seed);
        ledgerRepository.Setup(r => r.GetByIdAsync(conversationId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(ledger);
        peerIdentityRepository.Setup(r => r.GetOrCreateAsync(addPublicIdentityId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PeerIdentity(addPeerId, addPublicIdentityId));
        peerIdentityRepository.Setup(r => r.GetOrCreateAsync(removePublicIdentityId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PeerIdentity(removePeerId, removePublicIdentityId));
        ledgerRepository.Setup(r => r.UpdateGroupStateAsync(
            It.IsAny<RelayGroupLedger>(),
            It.IsAny<IReadOnlyList<ChatPeerId>>(),
            It.IsAny<IReadOnlyList<ChatPeerId>>(),
            It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var orchestrator = CreateOrchestrator(identityQueries, cryptoService, ledgerRepository, rosterQueries, publisher, peerIdentityRepository);

        // ACT
        var result = await orchestrator.ModifyGroupAsync(
            conversationId,
            baseEpoch,
            presentation,
            newEncryptedProfile,
            new List<PublicIdentityId> { addPublicIdentityId },
            new List<PublicIdentityId> { removePublicIdentityId },
            CancellationToken.None);

        // ASSERT
        result.Should().Be(RelayGroupOperationStatus.Success);
    }

    [Test]
    public async Task ModifyGroupAsync_WhenPeerIdentityRepositoryThrows_HandlesExceptionGracefully()
    {
        // ARRANGE
        var identityQueries = new Mock<ISelfIdentityQueries>();
        var cryptoService = new FakeGroupCryptographyService { ShouldVerifySucceed = true };
        var ledgerRepository = new Mock<IRelayGroupLedgerRepository>();
        var rosterQueries = new Mock<IRelayRosterQueries>();
        var publisher = new Mock<IRelayMessagePublisher>();
        var peerIdentityRepository = new Mock<IPeerIdentityRepository>();

        var conversationId = new ConversationId(Guid.NewGuid());
        var baseEpoch = 5U;
        var presentation = ZkPresentationBytes.FromBytesOwned(new byte[] { 0x01, 0x02 });
        var newEncryptedProfile = EncryptedGroupProfileBytes.FromBytesOwned(new byte[] { 0x05, 0x06 });
        var addPublicIdentityId = new PublicIdentityId(Guid.NewGuid());
        var seed = ZkServerSecretParamsSeedBytes.FromBytesOwned(new byte[32]);
        var encryptedProfile = EncryptedGroupProfileBytes.FromBytesOwned(new byte[] { 0x07, 0x08 });
        var ledger = new RelayGroupLedger(conversationId, 5, RelayGroupPublicParamsBytes.FromBytesOwned(new byte[32]), encryptedProfile, 1);

        identityQueries.Setup(q => q.GetZkServerSecretParamsSeedAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(seed);
        ledgerRepository.Setup(r => r.GetByIdAsync(conversationId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(ledger);
        peerIdentityRepository.Setup(r => r.GetOrCreateAsync(addPublicIdentityId, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Database error"));

        var orchestrator = CreateOrchestrator(identityQueries, cryptoService, ledgerRepository, rosterQueries, publisher, peerIdentityRepository);

        // ACT
        var act = async () => await orchestrator.ModifyGroupAsync(
            conversationId,
            baseEpoch,
            presentation,
            newEncryptedProfile,
            new List<PublicIdentityId> { addPublicIdentityId },
            new List<PublicIdentityId>(),
            CancellationToken.None);

        // ASSERT
        await act.Should().ThrowAsync<InvalidOperationException>();
    }
}

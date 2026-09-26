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
        Mock<IPeerIdentityRepository> peerIdentityRepository)
    {
        var logger = Mock.Of<ILogger<RelayGroupOrchestrator>>();
        var timeProvider = new FakeTimeProvider();
        return new RelayGroupOrchestrator(
            identityQueries.Object,
            cryptoService,
            ledgerRepository.Object,
            peerIdentityRepository.Object,
            timeProvider,
            logger);
    }






    [Test]
    public async Task GetGroupStateAsync_WhenValid_ReturnsLedger()
    {
        // ARRANGE
        var identityQueries = new Mock<ISelfIdentityQueries>();
        var cryptoService = new FakeGroupCryptographyService { ShouldVerifySucceed = true };
        var ledgerRepository = new Mock<IRelayGroupLedgerRepository>();
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

        var orchestrator = CreateOrchestrator(identityQueries, cryptoService, ledgerRepository, peerIdentityRepository);

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

        var orchestrator = CreateOrchestrator(identityQueries, cryptoService, ledgerRepository, peerIdentityRepository);

        // ACT
        var (status, returnedLedger) = await orchestrator.GetGroupStateAsync(
            conversationId, presentation, CancellationToken.None);

        // ASSERT
        status.Should().Be(RelayGroupOperationStatus.Unauthorized);
        returnedLedger.Should().BeNull();
    }




    [Test]
    public async Task GetGroupStateAsync_WhenGroupNotFound_ReturnsGroupNotFound()
    {
        // ARRANGE
        var identityQueries = new Mock<ISelfIdentityQueries>();
        var cryptoService = new FakeGroupCryptographyService { ShouldVerifySucceed = true };
        var ledgerRepository = new Mock<IRelayGroupLedgerRepository>();
        var peerIdentityRepository = new Mock<IPeerIdentityRepository>();

        var conversationId = new ConversationId(Guid.NewGuid());
        var presentation = ZkPresentationBytes.FromBytesOwned(new byte[] { 0x01, 0x02 });
        var seed = ZkServerSecretParamsSeedBytes.FromBytesOwned(new byte[32]);

        identityQueries.Setup(q => q.GetZkServerSecretParamsSeedAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(seed);
        ledgerRepository.Setup(r => r.GetByIdAsync(conversationId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((RelayGroupLedger?)null);

        var orchestrator = CreateOrchestrator(identityQueries, cryptoService, ledgerRepository, peerIdentityRepository);

        // ACT
        var (status, returnedLedger) = await orchestrator.GetGroupStateAsync(
            conversationId, presentation, CancellationToken.None);

        // ASSERT
        status.Should().Be(RelayGroupOperationStatus.GroupNotFound);
        returnedLedger.Should().BeNull();
    }


}

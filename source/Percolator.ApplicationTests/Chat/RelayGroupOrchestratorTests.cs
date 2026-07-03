using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using Percolator.Application.Chat;
using Percolator.Chat.GroupLedger;
using Percolator.Chat.GroupMembership;
using Percolator.Chat.Messaging.ValueObjects;
using Percolator.Cryptography;
using ChatPeerId = Percolator.Chat.GroupMembership.ChatPeerId;

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
    }

    private static RelayGroupOrchestrator CreateOrchestrator(
        Mock<ISelfIdentityQueries> identityQueries,
        FakeGroupCryptographyService cryptoService,
        Mock<IRelayGroupLedgerRepository> ledgerRepository,
        Mock<IRelayRosterQueries> rosterQueries,
        Mock<IRelayMessagePublisher> publisher,
        TimeProvider timeProvider)
    {
        var logger = Mock.Of<ILogger<RelayGroupOrchestrator>>();
        return new RelayGroupOrchestrator(
            identityQueries.Object,
            cryptoService,
            ledgerRepository.Object,
            rosterQueries.Object,
            publisher.Object,
            timeProvider,
            logger);
    }

    [Test]
    public async Task PublishGroupRelayMessageAsync_WhenVerificationFails_ThrowsUnauthorizedDomainException()
    {
        // ARRANGE
        var identityQueries = new Mock<ISelfIdentityQueries>();
        var cryptoService = new FakeGroupCryptographyService { ShouldVerifySucceed = false };
        var ledgerRepository = new Mock<IRelayGroupLedgerRepository>();
        var rosterQueries = new Mock<IRelayRosterQueries>();
        var publisher = new Mock<IRelayMessagePublisher>();
        var timeProvider = TimeProvider.System;

        var conversationId = new ConversationId(Guid.NewGuid());
        var requestedEpoch = 1U;
        var presentation = ZkPresentationBytes.FromBytesOwned(new byte[] { 0x01, 0x02 });
        var ciphertext = CiphertextBytes.FromBytesOwned(new byte[] { 0x03, 0x04 });
        var seed = ZkServerSecretParamsSeedBytes.FromBytesOwned(new byte[32]);
        var ledger = new RelayGroupLedger(conversationId, 0, RelayGroupPublicParamsBytes.FromBytesOwned(new byte[32]), 1);

        identityQueries.Setup(q => q.GetZkServerSecretParamsSeedAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(seed);
        ledgerRepository.Setup(r => r.GetByIdAsync(conversationId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(ledger);

        var orchestrator = CreateOrchestrator(identityQueries, cryptoService, ledgerRepository, rosterQueries, publisher, timeProvider);

        // ACT
        var act = async () => await orchestrator.PublishGroupRelayMessageAsync(
            conversationId, requestedEpoch, presentation, ciphertext, CancellationToken.None);

        // ASSERT
        await act.Should().ThrowAsync<UnauthorizedDomainException>()
            .WithMessage("Group presentation verification failed.");
    }

    [Test]
    public async Task PublishGroupRelayMessageAsync_WhenValid_CallsPublisherWithCorrectParameters()
    {
        // ARRANGE
        var identityQueries = new Mock<ISelfIdentityQueries>();
        var cryptoService = new FakeGroupCryptographyService { ShouldVerifySucceed = true };
        var ledgerRepository = new Mock<IRelayGroupLedgerRepository>();
        var rosterQueries = new Mock<IRelayRosterQueries>();
        var publisher = new Mock<IRelayMessagePublisher>();
        var timeProvider = TimeProvider.System;

        var conversationId = new ConversationId(Guid.NewGuid());
        var requestedEpoch = 1U;
        var presentation = ZkPresentationBytes.FromBytesOwned(new byte[] { 0x01, 0x02 });
        var ciphertext = CiphertextBytes.FromBytesOwned(new byte[] { 0x03, 0x04 });
        var seed = ZkServerSecretParamsSeedBytes.FromBytesOwned(new byte[32]);
        var ledger = new RelayGroupLedger(conversationId, 0, RelayGroupPublicParamsBytes.FromBytesOwned(new byte[32]), 1);
        var peerIds = new List<ChatPeerId> { new ChatPeerId(1), new ChatPeerId(2) };

        identityQueries.Setup(q => q.GetZkServerSecretParamsSeedAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(seed);
        ledgerRepository.Setup(r => r.GetByIdAsync(conversationId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(ledger);
        rosterQueries.Setup(r => r.GetMemberPeerIdsAsync(conversationId.Value, It.IsAny<CancellationToken>()))
            .ReturnsAsync(peerIds);

        var orchestrator = CreateOrchestrator(identityQueries, cryptoService, ledgerRepository, rosterQueries, publisher, timeProvider);

        // ACT
        await orchestrator.PublishGroupRelayMessageAsync(
            conversationId, requestedEpoch, presentation, ciphertext, CancellationToken.None);

        // ASSERT
        publisher.Verify(p => p.PublishAtomicAsync(
            It.Is<RelayGroupLedger>(l => l.ConversationId == conversationId && l.CurrentEpoch == requestedEpoch),
            It.Is<IReadOnlyList<ChatPeerId>>(ids => ids.Count == peerIds.Count),
            It.IsAny<QueuedPayloadBytes>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }
}

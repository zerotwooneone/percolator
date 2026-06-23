using FluentAssertions;
using Percolator.Chat.GroupLedger;
using Percolator.Chat.Messaging.ValueObjects;
using Percolator.Chat.GroupMembership;

namespace Percolator.Chat.Tests.GroupLedger;

[TestFixture]
public class RelayGroupLedgerTests
{
    [Test]
    public void AdvanceEpoch_WhenRequestedEpochIsGreaterThanCurrent_UpdatesCurrentEpoch()
    {
        // Arrange
        var conversationId = new ConversationId(Guid.NewGuid());
        var publicParams = RelayGroupPublicParamsBytes.FromBytesOwned(new byte[] { 1, 2, 3 });
        var ledger = new RelayGroupLedger(conversationId, currentEpoch: 1, publicParams, version: 1);

        // Act
        ledger.AdvanceEpoch(2);

        // Assert
        ledger.CurrentEpoch.Should().Be(2);
    }

    [Test]
    public void AdvanceEpoch_WhenRequestedEpochEqualsCurrent_ThrowsEpochConflictDomainException()
    {
        // Arrange
        var conversationId = new ConversationId(Guid.NewGuid());
        var publicParams = RelayGroupPublicParamsBytes.FromBytesOwned(new byte[] { 1, 2, 3 });
        var ledger = new RelayGroupLedger(conversationId, currentEpoch: 5, publicParams, version: 1);

        // Act
        var act = () => ledger.AdvanceEpoch(5);

        // Assert
        act.Should().Throw<EpochConflictDomainException>()
            .WithMessage("Requested epoch 5 is stale.");
    }

    [Test]
    public void AdvanceEpoch_WhenRequestedEpochIsLessThanCurrent_ThrowsEpochConflictDomainException()
    {
        // Arrange
        var conversationId = new ConversationId(Guid.NewGuid());
        var publicParams = RelayGroupPublicParamsBytes.FromBytesOwned(new byte[] { 1, 2, 3 });
        var ledger = new RelayGroupLedger(conversationId, currentEpoch: 10, publicParams, version: 1);

        // Act
        var act = () => ledger.AdvanceEpoch(5);

        // Assert
        act.Should().Throw<EpochConflictDomainException>()
            .WithMessage("Requested epoch 5 is stale.");
    }
}

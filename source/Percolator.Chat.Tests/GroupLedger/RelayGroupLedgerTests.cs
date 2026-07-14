using FluentAssertions;
using Percolator.Chat.GroupLedger;
using Percolator.Chat.Messaging.ValueObjects;

namespace Percolator.Chat.Tests.GroupLedger;

[TestFixture]
public class RelayGroupLedgerTests
{
    [Test]
    public void CanAcceptChatMessage_WhenRequestedEpochEqualsCurrent_ReturnsTrue()
    {
        // Arrange
        var conversationId = new ConversationId(Guid.NewGuid());
        var publicParams = RelayGroupPublicParamsBytes.FromBytesOwned(new byte[] { 1, 2, 3 });
        var encryptedProfile = EncryptedGroupProfileBytes.FromBytesOwned(new byte[] { 4, 5, 6 });
        var ledger = new RelayGroupLedger(conversationId, currentEpoch: 5, publicParams, encryptedProfile, version: 1);

        // Act
        var result = ledger.CanAcceptChatMessage(5);

        // Assert
        result.Should().BeTrue();
    }

    [Test]
    public void CanAcceptChatMessage_WhenRequestedEpochNotEqualsCurrent_ReturnsFalse()
    {
        // Arrange
        var conversationId = new ConversationId(Guid.NewGuid());
        var publicParams = RelayGroupPublicParamsBytes.FromBytesOwned(new byte[] { 1, 2, 3 });
        var encryptedProfile = EncryptedGroupProfileBytes.FromBytesOwned(new byte[] { 4, 5, 6 });
        var ledger = new RelayGroupLedger(conversationId, currentEpoch: 5, publicParams, encryptedProfile, version: 1);

        // Act
        var result = ledger.CanAcceptChatMessage(4);

        // Assert
        result.Should().BeFalse();
    }

    [Test]
    public void TryApplyMutation_WhenBaseEpochEqualsCurrent_ReturnsTrueAndAdvancesEpoch()
    {
        // Arrange
        var conversationId = new ConversationId(Guid.NewGuid());
        var publicParams = RelayGroupPublicParamsBytes.FromBytesOwned(new byte[] { 1, 2, 3 });
        var encryptedProfile = EncryptedGroupProfileBytes.FromBytesOwned(new byte[] { 4, 5, 6 });
        var ledger = new RelayGroupLedger(conversationId, currentEpoch: 5, publicParams, encryptedProfile, version: 1);
        var newProfile = EncryptedGroupProfileBytes.FromBytesOwned(new byte[] { 7, 8, 9 });

        // Act
        var result = ledger.TryApplyMutation(5, newProfile);

        // Assert
        result.Should().BeTrue();
        ledger.CurrentEpoch.Should().Be(6);
        ledger.EncryptedProfile.Should().Be(newProfile);
    }

    [Test]
    public void TryApplyMutation_WhenBaseEpochNotEqualsCurrent_ReturnsFalseAndDoesNotModify()
    {
        // Arrange
        var conversationId = new ConversationId(Guid.NewGuid());
        var publicParams = RelayGroupPublicParamsBytes.FromBytesOwned(new byte[] { 1, 2, 3 });
        var encryptedProfile = EncryptedGroupProfileBytes.FromBytesOwned(new byte[] { 4, 5, 6 });
        var ledger = new RelayGroupLedger(conversationId, currentEpoch: 5, publicParams, encryptedProfile, version: 1);
        var newProfile = EncryptedGroupProfileBytes.FromBytesOwned(new byte[] { 7, 8, 9 });
        var originalEpoch = ledger.CurrentEpoch;
        var originalProfile = ledger.EncryptedProfile;

        // Act
        var result = ledger.TryApplyMutation(4, newProfile);

        // Assert
        result.Should().BeFalse();
        ledger.CurrentEpoch.Should().Be(originalEpoch);
        ledger.EncryptedProfile.Should().Be(originalProfile);
    }

    [Test]
    public void TryApplyMutation_WhenCalledMultipleTimes_AdvancesEpochCorrectly()
    {
        // Arrange
        var conversationId = new ConversationId(Guid.NewGuid());
        var publicParams = RelayGroupPublicParamsBytes.FromBytesOwned(new byte[] { 1, 2, 3 });
        var encryptedProfile = EncryptedGroupProfileBytes.FromBytesOwned(new byte[] { 4, 5, 6 });
        var ledger = new RelayGroupLedger(conversationId, currentEpoch: 5, publicParams, encryptedProfile, version: 1);
        var newProfile1 = EncryptedGroupProfileBytes.FromBytesOwned(new byte[] { 7, 8, 9 });
        var newProfile2 = EncryptedGroupProfileBytes.FromBytesOwned(new byte[] { 10, 11, 12 });

        // Act - First mutation
        var result1 = ledger.TryApplyMutation(5, newProfile1);

        // Assert - First mutation
        result1.Should().BeTrue();
        ledger.CurrentEpoch.Should().Be(6);
        ledger.EncryptedProfile.Should().Be(newProfile1);

        // Act - Second mutation
        var result2 = ledger.TryApplyMutation(6, newProfile2);

        // Assert - Second mutation
        result2.Should().BeTrue();
        ledger.CurrentEpoch.Should().Be(7);
        ledger.EncryptedProfile.Should().Be(newProfile2);
    }

    [Test]
    public void CanAcceptChatMessage_WhenEpochIsZero_ReturnsTrueForZero()
    {
        // Arrange
        var conversationId = new ConversationId(Guid.NewGuid());
        var publicParams = RelayGroupPublicParamsBytes.FromBytesOwned(new byte[] { 1, 2, 3 });
        var encryptedProfile = EncryptedGroupProfileBytes.FromBytesOwned(new byte[] { 4, 5, 6 });
        var ledger = new RelayGroupLedger(conversationId, currentEpoch: 0, publicParams, encryptedProfile, version: 1);

        // Act
        var result = ledger.CanAcceptChatMessage(0);

        // Assert
        result.Should().BeTrue();
    }

    [Test]
    public void Constructor_WhenCalled_InitializesPropertiesCorrectly()
    {
        // Arrange
        var conversationId = new ConversationId(Guid.NewGuid());
        var publicParams = RelayGroupPublicParamsBytes.FromBytesOwned(new byte[] { 1, 2, 3 });
        var encryptedProfile = EncryptedGroupProfileBytes.FromBytesOwned(new byte[] { 4, 5, 6 });
        var currentEpoch = 5U;
        var version = 2;

        // Act
        var ledger = new RelayGroupLedger(conversationId, currentEpoch, publicParams, encryptedProfile, version);

        // Assert
        ledger.ConversationId.Should().Be(conversationId);
        ledger.CurrentEpoch.Should().Be(currentEpoch);
        ledger.GroupPublicParams.Should().Be(publicParams);
        ledger.EncryptedProfile.Should().Be(encryptedProfile);
        ledger.Version.Should().Be(version);
    }

    [Test]
    public void CanAcceptChatMessage_WhenCalledWithMaxUintValue_ReturnsCorrectResult()
    {
        // Arrange
        var conversationId = new ConversationId(Guid.NewGuid());
        var publicParams = RelayGroupPublicParamsBytes.FromBytesOwned(new byte[] { 1, 2, 3 });
        var encryptedProfile = EncryptedGroupProfileBytes.FromBytesOwned(new byte[] { 4, 5, 6 });
        var ledger = new RelayGroupLedger(conversationId, currentEpoch: uint.MaxValue, publicParams, encryptedProfile, version: 1);

        // Act
        var result = ledger.CanAcceptChatMessage(uint.MaxValue);

        // Assert
        result.Should().BeTrue();
    }
}

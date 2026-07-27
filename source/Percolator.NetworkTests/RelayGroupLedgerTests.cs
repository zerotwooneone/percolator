using Percolator.Network.RelayLedger;
using Percolator.Network.ValueObjects;

namespace Percolator.NetworkTests;

[TestFixture]
public class RelayGroupLedgerTests
{
    [Test]
    public void OverwriteState_WhenBaseEpochMatches_UpdatesBlobAndIncrementsEpoch()
    {
        // Arrange
        var groupId = new RelayGroupId(Guid.NewGuid());
        var initialBlob = EncryptedEntriesBlobBytes.FromBytesOwned(new byte[] { 1, 2, 3 });
        var ledger = RelayGroupLedger.CreateNew(groupId, initialBlob);
        var newBlob = EncryptedEntriesBlobBytes.FromBytesOwned(new byte[] { 4, 5, 6 });
        var baseEpoch = ledger.CurrentEpoch;

        // Act
        ledger.OverwriteState(baseEpoch, newBlob);

        // Assert
        Assert.That(ledger.CurrentEpoch, Is.EqualTo(new RelayGroupEpoch(1)));
        Assert.That(ledger.EncryptedEntriesBlob, Is.EqualTo(newBlob));
        Assert.That(ledger.ConcurrencyVersion, Is.EqualTo(1));
    }

    [Test]
    public void OverwriteState_WhenBaseEpochDiffers_ThrowsInvalidOperationException()
    {
        // Arrange
        var groupId = new RelayGroupId(Guid.NewGuid());
        var initialBlob = EncryptedEntriesBlobBytes.FromBytesOwned(new byte[] { 1, 2, 3 });
        var ledger = RelayGroupLedger.CreateNew(groupId, initialBlob);
        var newBlob = EncryptedEntriesBlobBytes.FromBytesOwned(new byte[] { 4, 5, 6 });
        var wrongEpoch = new RelayGroupEpoch(999);

        // Act & Assert
        var exception = Assert.Throws<InvalidOperationException>(() => ledger.OverwriteState(wrongEpoch, newBlob));
        Assert.That(exception.Message, Does.Contain("Epoch conflict"));
        
        // Verify state unchanged
        Assert.That(ledger.CurrentEpoch, Is.EqualTo(new RelayGroupEpoch(0)));
        Assert.That(ledger.EncryptedEntriesBlob, Is.EqualTo(initialBlob));
        Assert.That(ledger.ConcurrencyVersion, Is.EqualTo(0));
    }
}

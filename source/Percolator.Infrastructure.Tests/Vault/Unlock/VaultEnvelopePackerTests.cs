namespace Percolator.Infrastructure.Tests.Vault.Unlock;

using System.Security.Cryptography;
using Percolator.Domain.Security.ValueObjects;
using Percolator.Infrastructure.Vault;

[TestFixture]
public class VaultEnvelopePackerTests
{
    private VaultEnvelopePacker _packer = null!;

    [SetUp]
    public void SetUp()
    {
        // Use lightweight Argon2 parameters for fast unit tests (1 MB, 1 iter, 1 thread)
        _packer = new VaultEnvelopePacker(memoryKb: 1024, iterations: 1, parallelism: 1);
    }

    [Test]
    public void MasterPasswordChange_RotatesMasterSalt_PreservesDuress()
    {
        // Arrange
        var masterPass = VaultPassphrase.Create("MasterPassword123!");
        var duressPass = VaultPassphrase.Create("DuressPassword456!");
        byte[] dbKey = RandomNumberGenerator.GetBytes(32);

        var createResult = _packer.CreateEnvelope(masterPass, duressPass, dbKey);
        createResult.IsSuccess.Should().BeTrue();
        var initialHeader = createResult.Value;

        byte[] initialMasterSalt = initialHeader.MasterSlotSalt.ToByteArray();
        byte[] initialDuressSalt = initialHeader.DuressSlotSalt.ToByteArray();
        byte[] initialDuressPayload = initialHeader.DuressSlotPayload.ToByteArray();

        // Act: Re-wrap master slot with new password
        var newMasterPass = VaultPassphrase.Create("NewMasterPassword789!");
        var rewrapResult = _packer.RewrapMasterSlot(initialHeader, newMasterPass, dbKey);
        rewrapResult.IsSuccess.Should().BeTrue();
        var updatedHeader = rewrapResult.Value;

        // Assert: Master salt rotated, Duress slot untouched
        updatedHeader.MasterSlotSalt.ToByteArray().Should().NotEqual(initialMasterSalt);
        updatedHeader.DuressSlotSalt.ToByteArray().Should().Equal(initialDuressSalt);
        updatedHeader.DuressSlotPayload.ToByteArray().Should().Equal(initialDuressPayload);

        // Verify new master unlocks and produces exact original dbKey
        byte[] unlockedKey = new byte[32];
        var masterTrial = _packer.EvaluateSlots(updatedHeader, newMasterPass, unlockedKey);
        masterTrial.Outcome.Should().Be(SlotTrialOutcome.MasterUnlocked);
        unlockedKey.Should().Equal(dbKey);

        // Verify old master fails
        var oldTrial = _packer.EvaluateSlots(updatedHeader, masterPass, unlockedKey);
        oldTrial.Outcome.Should().Be(SlotTrialOutcome.InvalidCredentials);

        // Verify duress password still triggers duress
        var duressTrial = _packer.EvaluateSlots(updatedHeader, duressPass, unlockedKey);
        duressTrial.Outcome.Should().Be(SlotTrialOutcome.DuressTriggered);
    }

    [Test]
    public void EvaluateSlots_InvalidPassword_ReturnsInvalidCredentials()
    {
        var masterPass = VaultPassphrase.Create("CorrectPassword");
        byte[] dbKey = RandomNumberGenerator.GetBytes(32);
        var envelope = _packer.CreateEnvelope(masterPass, null, dbKey).Value;

        var wrongPass = VaultPassphrase.Create("WrongPassword");
        byte[] destKey = new byte[32];
        var result = _packer.EvaluateSlots(envelope, wrongPass, destKey);

        result.Outcome.Should().Be(SlotTrialOutcome.InvalidCredentials);
    }

    [Test]
    public void ConfigureDuressSlot_UpdatesAndClearsDuressProtection()
    {
        var masterPass = VaultPassphrase.Create("MasterSecret");
        byte[] dbKey = RandomNumberGenerator.GetBytes(32);
        var initialHeader = _packer.CreateEnvelope(masterPass, null, dbKey).Value;

        // Initially no duress
        var duressPass = VaultPassphrase.Create("DuressSecret");
        byte[] destKey = new byte[32];
        _packer.EvaluateSlots(initialHeader, duressPass, destKey).Outcome.Should().Be(SlotTrialOutcome.InvalidCredentials);

        // 1. Configure duress
        var withDuress = _packer.ConfigureDuressSlot(initialHeader, duressPass).Value;
        _packer.EvaluateSlots(withDuress, duressPass, destKey).Outcome.Should().Be(SlotTrialOutcome.DuressTriggered);

        // 2. Clear duress
        var cleared = _packer.ConfigureDuressSlot(withDuress, null).Value;
        _packer.EvaluateSlots(cleared, duressPass, destKey).Outcome.Should().Be(SlotTrialOutcome.InvalidCredentials);
    }
}

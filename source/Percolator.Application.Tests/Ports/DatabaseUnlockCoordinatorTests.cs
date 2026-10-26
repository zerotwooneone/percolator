using FluentAssertions;
using NUnit.Framework;
using Percolator.Application2.Ports;
using Percolator.Application.Tests.TestDoubles;
using Percolator.Domain.Security.ValueObjects;

namespace Percolator.Application.Tests.Ports;

[TestFixture]
public sealed class DatabaseUnlockCoordinatorTests
{
    private sealed class TrackingDuressWipeHandler : IDuressWipeHandler
    {
        public bool WipeExecuted { get; private set; }
        public int WipeCallCount { get; private set; }

        public Task ExecuteWipeAsync(CancellationToken ct = default)
        {
            WipeExecuted = true;
            WipeCallCount++;
            return Task.CompletedTask;
        }
    }

    [Test]
    public async Task DetermineStatusAsync_WhenUninitialized_ReturnsUninitialized()
    {
        var coordinator = new FakeDatabaseUnlockCoordinator();

        var status = await coordinator.DetermineStatusAsync();

        status.Should().Be(DatabaseSetupStatus.Uninitialized);
        coordinator.Status.Should().Be(DatabaseSetupStatus.Uninitialized);
    }

    [Test]
    public async Task InitializeAsync_WithValidMasterPassword_TransitionsToUnlockedAndReturnsSession()
    {
        var coordinator = new FakeDatabaseUnlockCoordinator();
        using var master = VaultPassphrase.Create("ValidMasterPassword123!");

        var result = await coordinator.InitializeAsync(master);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().NotBeNull();
        coordinator.Status.Should().Be(DatabaseSetupStatus.Unlocked);
    }

    [Test]
    public async Task InitializeAsync_WithBlankMasterPassword_Succeeds()
    {
        var coordinator = new FakeDatabaseUnlockCoordinator();
        using var blank = VaultPassphrase.Empty;

        var result = await coordinator.InitializeAsync(blank);

        result.IsSuccess.Should().BeTrue();
        coordinator.Status.Should().Be(DatabaseSetupStatus.Unlocked);

        // Can lock and unlock with blank password
        await coordinator.LockAsync();
        coordinator.Status.Should().Be(DatabaseSetupStatus.Locked);

        using var candidateBlank = VaultPassphrase.Create("");
        var unlockResult = await coordinator.UnlockAsync(candidateBlank);
        unlockResult.Should().BeOfType<UnlockResult.Success>();
    }

    [Test]
    public async Task InitializeAsync_WhenDuressMatchesMaster_ReturnsDomainFailure()
    {
        var coordinator = new FakeDatabaseUnlockCoordinator();
        using var master = VaultPassphrase.Create("IdenticalPassphrase_42");
        using var duress = VaultPassphrase.Create("IdenticalPassphrase_42");

        var result = await coordinator.InitializeAsync(master, duress);

        result.IsFailure.Should().BeTrue();
        result.Error.Description.Should().Contain("cannot match");
        coordinator.Status.Should().Be(DatabaseSetupStatus.Uninitialized);
    }

    [Test]
    public async Task UnlockAsync_WithValidMasterPassword_ReturnsSuccessAndTransitionsToUnlocked()
    {
        var coordinator = new FakeDatabaseUnlockCoordinator();
        using var master = VaultPassphrase.Create("MySecretVault_1");
        await coordinator.InitializeAsync(master);
        await coordinator.LockAsync();

        using var candidate = VaultPassphrase.Create("MySecretVault_1");
        var result = await coordinator.UnlockAsync(candidate);

        result.Should().BeOfType<UnlockResult.Success>();
        var success = (UnlockResult.Success)result;
        success.Session.Should().NotBeNull();
        coordinator.Status.Should().Be(DatabaseSetupStatus.Unlocked);
    }

    [Test]
    public async Task UnlockAsync_WithIncorrectPassword_ReturnsInvalidCredentials()
    {
        var coordinator = new FakeDatabaseUnlockCoordinator();
        using var master = VaultPassphrase.Create("CorrectPassword");
        await coordinator.InitializeAsync(master);
        await coordinator.LockAsync();

        using var wrongCandidate = VaultPassphrase.Create("WrongPassword_Attempt");
        var result = await coordinator.UnlockAsync(wrongCandidate);

        result.Should().BeOfType<UnlockResult.InvalidCredentials>();
        coordinator.Status.Should().Be(DatabaseSetupStatus.Locked);
    }

    [Test]
    public async Task UnlockAsync_WithDuressPassword_ExecutesWipe_TransitionsToUninitialized_AndReturnsDuressActivated()
    {
        var wipeHandler = new TrackingDuressWipeHandler();
        var coordinator = new FakeDatabaseUnlockCoordinator(wipeHandler);

        using var master = VaultPassphrase.Create("MasterKey_Normal");
        using var duress = VaultPassphrase.Create("DuressKey_Emergency");
        await coordinator.InitializeAsync(master, duress);
        await coordinator.LockAsync();

        using var candidateDuress = VaultPassphrase.Create("DuressKey_Emergency");
        var result = await coordinator.UnlockAsync(candidateDuress);

        result.Should().BeOfType<UnlockResult.DuressActivated>();
        wipeHandler.WipeExecuted.Should().BeTrue();
        wipeHandler.WipeCallCount.Should().Be(1);
        coordinator.Status.Should().Be(DatabaseSetupStatus.Uninitialized);

        // After wipe, subsequent unlocks fail
        using var candidateMaster = VaultPassphrase.Create("MasterKey_Normal");
        var postWipeUnlock = await coordinator.UnlockAsync(candidateMaster);
        postWipeUnlock.Should().BeOfType<UnlockResult.InvalidCredentials>();
    }

    [Test]
    public async Task ChangeMasterPasswordAsync_WithValidCurrentPassword_UpdatesMasterPassword()
    {
        var coordinator = new FakeDatabaseUnlockCoordinator();
        using var initialMaster = VaultPassphrase.Create("InitialMasterPass_1");
        await coordinator.InitializeAsync(initialMaster);

        using var currentVerify = VaultPassphrase.Create("InitialMasterPass_1");
        using var newMaster = VaultPassphrase.Create("NewUpdatedMasterPass_2");
        var changeResult = await coordinator.ChangeMasterPasswordAsync(currentVerify, newMaster);

        changeResult.IsSuccess.Should().BeTrue();

        // Relock and verify new password unlocks while old password fails
        await coordinator.LockAsync();

        using var oldAttempt = VaultPassphrase.Create("InitialMasterPass_1");
        var oldUnlockResult = await coordinator.UnlockAsync(oldAttempt);
        oldUnlockResult.Should().BeOfType<UnlockResult.InvalidCredentials>();

        using var newAttempt = VaultPassphrase.Create("NewUpdatedMasterPass_2");
        var newUnlockResult = await coordinator.UnlockAsync(newAttempt);
        newUnlockResult.Should().BeOfType<UnlockResult.Success>();
    }

    [Test]
    public async Task ChangeMasterPasswordAsync_WithInvalidCurrentPassword_ReturnsFailureAndPreservesMasterPassword()
    {
        var coordinator = new FakeDatabaseUnlockCoordinator();
        using var initialMaster = VaultPassphrase.Create("InitialMasterPass_1");
        await coordinator.InitializeAsync(initialMaster);

        using var wrongCurrent = VaultPassphrase.Create("WrongCurrentAttempt");
        using var newMaster = VaultPassphrase.Create("NewAttemptedPass");
        var changeResult = await coordinator.ChangeMasterPasswordAsync(wrongCurrent, newMaster);

        changeResult.IsFailure.Should().BeTrue();

        // Relock and assert initial password is still valid
        await coordinator.LockAsync();

        using var originalAttempt = VaultPassphrase.Create("InitialMasterPass_1");
        var unlockResult = await coordinator.UnlockAsync(originalAttempt);
        unlockResult.Should().BeOfType<UnlockResult.Success>();
    }

    [Test]
    public async Task ChangeMasterPasswordAsync_WhenMatchingConfiguredDuress_ReturnsFailure()
    {
        var coordinator = new FakeDatabaseUnlockCoordinator();
        using var initialMaster = VaultPassphrase.Create("MasterPass_1");
        using var duress = VaultPassphrase.Create("DuressPass_2");
        await coordinator.InitializeAsync(initialMaster, duress);

        using var currentVerify = VaultPassphrase.Create("MasterPass_1");
        using var newMasterColliding = VaultPassphrase.Create("DuressPass_2");
        var changeResult = await coordinator.ChangeMasterPasswordAsync(currentVerify, newMasterColliding);

        changeResult.IsFailure.Should().BeTrue();
        changeResult.Error.Description.Should().Contain("cannot match");
    }

    [Test]
    public async Task ConfigureDuressPasswordAsync_WhenValid_AllowsUpdatingAndWipingWithNewDuress()
    {
        var wipeHandler = new TrackingDuressWipeHandler();
        var coordinator = new FakeDatabaseUnlockCoordinator(wipeHandler);
        using var master = VaultPassphrase.Create("MasterPass");
        await coordinator.InitializeAsync(master);

        using var newDuress = VaultPassphrase.Create("NewDuressCode_911");
        var configResult = await coordinator.ConfigureDuressPasswordAsync(newDuress);

        configResult.IsSuccess.Should().BeTrue();

        await coordinator.LockAsync();

        using var duressCandidate = VaultPassphrase.Create("NewDuressCode_911");
        var unlockResult = await coordinator.UnlockAsync(duressCandidate);

        unlockResult.Should().BeOfType<UnlockResult.DuressActivated>();
        wipeHandler.WipeExecuted.Should().BeTrue();
    }

    [Test]
    public async Task ConfigureDuressPasswordAsync_WithNullOrEmpty_ClearsDuressPassphrase()
    {
        var wipeHandler = new TrackingDuressWipeHandler();
        var coordinator = new FakeDatabaseUnlockCoordinator(wipeHandler);
        using var master = VaultPassphrase.Create("MasterPass");
        using var duress = VaultPassphrase.Create("InitialDuress_911");
        await coordinator.InitializeAsync(master, duress);

        // Clear duress
        var clearResult = await coordinator.ConfigureDuressPasswordAsync(null);
        clearResult.IsSuccess.Should().BeTrue();

        await coordinator.LockAsync();

        // Former duress password now yields InvalidCredentials and does NOT execute wipe
        using var oldDuressAttempt = VaultPassphrase.Create("InitialDuress_911");
        var unlockResult = await coordinator.UnlockAsync(oldDuressAttempt);

        unlockResult.Should().BeOfType<UnlockResult.InvalidCredentials>();
        wipeHandler.WipeExecuted.Should().BeFalse();
    }

    [Test]
    public async Task ConfigureDuressPasswordAsync_WhenMatchingMaster_ReturnsFailure()
    {
        var coordinator = new FakeDatabaseUnlockCoordinator();
        using var master = VaultPassphrase.Create("MasterPass_123");
        await coordinator.InitializeAsync(master);

        using var collidingDuress = VaultPassphrase.Create("MasterPass_123");
        var configResult = await coordinator.ConfigureDuressPasswordAsync(collidingDuress);

        configResult.IsFailure.Should().BeTrue();
        configResult.Error.Description.Should().Contain("cannot match");
    }

    [Test]
    public async Task LockAsync_StopsAndDisposesActiveSession_AndTransitionsToLocked()
    {
        var coordinator = new FakeDatabaseUnlockCoordinator();
        using var master = VaultPassphrase.Create("MasterPass");
        var initResult = await coordinator.InitializeAsync(master);

        var session = (FakeAuthenticatedSession)initResult.Value;
        await session.StartAsync();
        session.IsStarted.Should().BeTrue();

        await coordinator.LockAsync();

        coordinator.Status.Should().Be(DatabaseSetupStatus.Locked);
        session.IsStopped.Should().BeTrue();
        session.IsDisposed.Should().BeTrue();
    }
}

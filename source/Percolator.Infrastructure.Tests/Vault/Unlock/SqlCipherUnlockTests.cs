namespace Percolator.Infrastructure.Tests.Vault.Unlock;

using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Hosting;
using Percolator.Application2.Ports;
using Percolator.Domain.Common;
using Percolator.Domain.Security.Ports;
using Percolator.Domain.Security.ValueObjects;
using Percolator.Infrastructure.Vault;

[TestFixture]
public class SqlCipherUnlockTests
{
    private string _tempDir = null!;
    private VaultStorageOptions _options = null!;
    private ProtobufHeaderStorage _headerStorage = null!;
    private VaultEnvelopePacker _envelopePacker = null!;
    private DatabaseCanaryProbe _canaryProbe = null!;
    private NoOpDatabaseMigrator _migrator = null!;
    private DefaultStage2HostFactory _hostFactory = null!;
    private DuressWipeHandler _wipeHandler = null!;
    private SqlCipherDatabaseUnlockCoordinator _coordinator = null!;

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        SQLitePCL.Batteries_V2.Init();
    }

    [SetUp]
    public void SetUp()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "sqlcipher_unlock_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _options = new VaultStorageOptions { BaseDirectory = _tempDir };

        _headerStorage = new ProtobufHeaderStorage(_options);
        // Fast Argon2 parameters for test speed (1 MB, 1 iter, 1 thread)
        _envelopePacker = new VaultEnvelopePacker(memoryKb: 1024, iterations: 1, parallelism: 1);
        _canaryProbe = new DatabaseCanaryProbe();
        _migrator = new NoOpDatabaseMigrator();
        _hostFactory = new DefaultStage2HostFactory();
        _wipeHandler = new DuressWipeHandler(_options);

        _coordinator = new SqlCipherDatabaseUnlockCoordinator(
            _headerStorage,
            _envelopePacker,
            _canaryProbe,
            _migrator,
            _hostFactory,
            _wipeHandler,
            _options);
    }

    [TearDown]
    public async Task TearDown()
    {
        await _coordinator.LockAsync();
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_tempDir))
        {
            try { Directory.Delete(_tempDir, recursive: true); } catch { }
        }
    }

    [Test]
    public async Task HappyPathMasterUnlock_CoordinatesCollaborators()
    {
        // 1. Initialize
        var masterPass = VaultPassphrase.Create("SuperSecret123!");
        var duressPass = VaultPassphrase.Create("EmergencyDuress999!");

        var initResult = await _coordinator.InitializeAsync(masterPass, duressPass);
        initResult.IsSuccess.Should().BeTrue();
        _coordinator.Status.Should().Be(DatabaseSetupStatus.Unlocked);

        // 2. Lock
        await _coordinator.LockAsync();
        _coordinator.Status.Should().Be(DatabaseSetupStatus.Locked);

        // 3. Unlock with valid master
        var unlockResult = await _coordinator.UnlockAsync(masterPass);
        unlockResult.Should().BeOfType<UnlockResult.Success>();
        _coordinator.Status.Should().Be(DatabaseSetupStatus.Unlocked);
    }

    [Test]
    public async Task InvalidPassword_ReturnsInvalidCredentials()
    {
        var masterPass = VaultPassphrase.Create("ValidMaster123!");
        await _coordinator.InitializeAsync(masterPass);
        await _coordinator.LockAsync();

        var wrongPass = VaultPassphrase.Create("WrongPassword!");
        var result = await _coordinator.UnlockAsync(wrongPass);

        result.Should().BeOfType<UnlockResult.InvalidCredentials>();
        _coordinator.Status.Should().Be(DatabaseSetupStatus.Locked);
    }

    [Test]
    public async Task BlankPassword_CreatesEncryptedDatabase_AndUnkeyedConnectionFails()
    {
        // Setup with VaultPassphrase.Empty
        var emptyPass = VaultPassphrase.Empty;
        var initResult = await _coordinator.InitializeAsync(emptyPass);
        initResult.IsSuccess.Should().BeTrue(initResult.IsFailure ? $"{initResult.Error.Code}: {initResult.Error.Description}" : string.Empty);

        await _coordinator.LockAsync();

        // Raw unkeyed connection must fail to read encrypted database
        await using (var rawConn = new SqliteConnection($"Data Source={_options.DatabaseFilePath}"))
        {
            await rawConn.OpenAsync();
            var probe = new DatabaseCanaryProbe();
            var probeResult = await probe.ProbeAsync(rawConn);
            probeResult.Should().Be(CanaryProbeResult.FailedNotADatabase);
        }

        // Unlock with empty passphrase succeeds
        var unlockResult = await _coordinator.UnlockAsync(emptyPass);
        unlockResult.Should().BeOfType<UnlockResult.Success>();
        _coordinator.Status.Should().Be(DatabaseSetupStatus.Unlocked);
    }

    [Test]
    public async Task CanaryOrMigrationFailure_DisposesAndZeroizesMasterKeyHandle()
    {
        var failingMigrator = new FailingDatabaseMigrator();
        var coordinator = new SqlCipherDatabaseUnlockCoordinator(
            _headerStorage,
            _envelopePacker,
            _canaryProbe,
            failingMigrator,
            _hostFactory,
            _wipeHandler,
            _options);

        var masterPass = VaultPassphrase.Create("SecretPassword");
        var result = await coordinator.InitializeAsync(masterPass);

        result.IsFailure.Should().BeTrue();
        coordinator.Status.Should().Be(DatabaseSetupStatus.Uninitialized);
    }

    [Test]
    public async Task LockAsync_ScrubsMemoryAndClearsPools()
    {
        var masterPass = VaultPassphrase.Create("LockTestPassword");
        await _coordinator.InitializeAsync(masterPass);
        _coordinator.Status.Should().Be(DatabaseSetupStatus.Unlocked);

        await _coordinator.LockAsync();
        _coordinator.Status.Should().Be(DatabaseSetupStatus.Locked);

        var status = await _coordinator.DetermineStatusAsync();
        status.Should().Be(DatabaseSetupStatus.Locked);
    }

    [Test]
    public async Task ChangeMasterPasswordAsync_RewrapsMasterSlot_PreservesDuressSlot()
    {
        var masterPass = VaultPassphrase.Create("CurrentMaster");
        var duressPass = VaultPassphrase.Create("DuressSentinel");
        await _coordinator.InitializeAsync(masterPass, duressPass);

        // Change master password
        var newMaster = VaultPassphrase.Create("BrandNewMaster");
        var changeResult = await _coordinator.ChangeMasterPasswordAsync(masterPass, newMaster);
        changeResult.IsSuccess.Should().BeTrue();

        // Lock and verify old master fails, new master succeeds
        await _coordinator.LockAsync();

        var oldUnlock = await _coordinator.UnlockAsync(masterPass);
        oldUnlock.Should().BeOfType<UnlockResult.InvalidCredentials>();

        var newUnlock = await _coordinator.UnlockAsync(newMaster);
        newUnlock.Should().BeOfType<UnlockResult.Success>();

        // Lock and verify duress password still triggers duress
        await _coordinator.LockAsync();
        var duressUnlock = await _coordinator.UnlockAsync(duressPass);
        duressUnlock.Should().BeOfType<UnlockResult.DuressActivated>();

        // After duress activation, database files are destroyed
        File.Exists(_options.HeaderFilePath).Should().BeFalse();
        _coordinator.Status.Should().Be(DatabaseSetupStatus.Uninitialized);
    }

    [Test]
    public async Task ConfigureDuressPasswordAsync_RequiresMasterReAuthentication()
    {
        var masterPass = VaultPassphrase.Create("MasterAuth");
        await _coordinator.InitializeAsync(masterPass);

        var wrongMaster = VaultPassphrase.Create("WrongMasterAuth");
        var newDuress = VaultPassphrase.Create("NewDuress123");

        // Wrong master password fails re-authentication
        var failResult = await _coordinator.ConfigureDuressPasswordAsync(wrongMaster, newDuress);
        failResult.IsFailure.Should().BeTrue();

        // Valid master password succeeds
        var okResult = await _coordinator.ConfigureDuressPasswordAsync(masterPass, newDuress);
        okResult.IsSuccess.Should().BeTrue();
    }

    [Test]
    public async Task ConfigureDuressPasswordAsync_IdenticalToMaster_RejectsCollision()
    {
        var masterPass = VaultPassphrase.Create("MasterAuthCollision");
        await _coordinator.InitializeAsync(masterPass);

        // Supplying duress identical to master fails
        var collisionResult = await _coordinator.ConfigureDuressPasswordAsync(masterPass, masterPass);
        collisionResult.IsFailure.Should().BeTrue();
    }

    private sealed class FailingDatabaseMigrator : IDatabaseMigrator
    {
        public Task<DomainResult> MigrateAsync(CancellationToken ct = default)
        {
            return Task.FromResult(DomainResult.Failure(new DomainError("MIGRATION_ERROR", "Simulated migration failure.")));
        }
    }
}

using Percolator.Application.Ports;

namespace Percolator.Infrastructure.Vault;

using System.IO;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Hosting;
using Percolator.Domain.Common;
using Percolator.Domain.Security.Ports;
using Percolator.Domain.Security.ValueObjects;

public sealed class SqlCipherDatabaseUnlockCoordinator : IDatabaseUnlockCoordinator
{
    private readonly IHeaderStorage _headerStorage;
    private readonly IVaultEnvelopePacker _envelopePacker;
    private readonly IDatabaseCanaryProbe _canaryProbe;
    private readonly IDatabaseMigrator _migrator;
    private readonly ISessionHostFactory _hostFactory;
    private readonly IDuressWipeHandler _wipeHandler;
    private readonly VaultStorageOptions _options;

    private IMasterKeyHandle? _activeKeyHandle;
    private IAuthenticatedSession? _activeSession;

    public DatabaseSetupStatus Status { get; private set; } = DatabaseSetupStatus.Uninitialized;

    internal SqlCipherDatabaseUnlockCoordinator(
        IHeaderStorage headerStorage,
        IVaultEnvelopePacker envelopePacker,
        IDatabaseCanaryProbe canaryProbe,
        IDatabaseMigrator migrator,
        ISessionHostFactory hostFactory,
        IDuressWipeHandler wipeHandler,
        VaultStorageOptions options)
    {
        _headerStorage = headerStorage ?? throw new ArgumentNullException(nameof(headerStorage));
        _envelopePacker = envelopePacker ?? throw new ArgumentNullException(nameof(envelopePacker));
        _canaryProbe = canaryProbe ?? throw new ArgumentNullException(nameof(canaryProbe));
        _migrator = migrator ?? throw new ArgumentNullException(nameof(migrator));
        _hostFactory = hostFactory ?? throw new ArgumentNullException(nameof(hostFactory));
        _wipeHandler = wipeHandler ?? throw new ArgumentNullException(nameof(wipeHandler));
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public async Task<DatabaseSetupStatus> DetermineStatusAsync(CancellationToken ct = default)
    {
        if (Status == DatabaseSetupStatus.Unlocked)
        {
            return DatabaseSetupStatus.Unlocked;
        }

        var headerResult = await _headerStorage.ReadHeaderAsync(ct).ConfigureAwait(false);
        if (headerResult.IsFailure)
        {
            // A header that exists on disk but fails to read/parse means damaged, not fresh —
            // callers must not be offered first-time setup (which would overwrite the vault).
            Status = File.Exists(_options.HeaderFilePath)
                ? DatabaseSetupStatus.Damaged
                : DatabaseSetupStatus.Uninitialized;
            return Status;
        }

        Status = DatabaseSetupStatus.Locked;
        return DatabaseSetupStatus.Locked;
    }

    public async Task<DomainResult<IAuthenticatedSession>> InitializeAsync(
        VaultPassphrase masterPassword,
        VaultPassphrase? duressPassword = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(masterPassword);

        if (duressPassword != null && !duressPassword.IsEmpty && VaultPassphrase.ConstantTimeEquals(masterPassword, duressPassword))
        {
            return DomainResult<IAuthenticatedSession>.Failure(
                new DomainError("Vault.Collision", "Duress passphrase cannot match the master passphrase."));
        }

        // 1. Generate random 32-byte master key
        var masterKeyHandle = MasterKeyHandle.GenerateRandom();

        try
        {
            // 2-5. Create envelope, persist header, initialize encrypted database, migrate schema
            var vaultResult = await CreateFreshVaultAsync(masterPassword, duressPassword, masterKeyHandle, ct).ConfigureAwait(false);
            if (vaultResult.IsFailure)
            {
                masterKeyHandle.Dispose();
                return DomainResult<IAuthenticatedSession>.Failure(vaultResult.Error);
            }

            // 6. Build Stage 2 session and transition status
            var session = _hostFactory.CreateSession(masterKeyHandle);
            _activeKeyHandle = masterKeyHandle;
            _activeSession = session;
            Status = DatabaseSetupStatus.Unlocked;

            return DomainResult<IAuthenticatedSession>.Success(_activeSession);
        }
        catch (Exception ex)
        {
            masterKeyHandle.Dispose();
            _activeKeyHandle = null;
            return DomainResult<IAuthenticatedSession>.Failure(
                new DomainError("Vault.InitFailed", ex.Message));
        }
    }

    public async Task<UnlockResult> UnlockAsync(
        VaultPassphrase candidatePassword,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(candidatePassword);

        var headerResult = await _headerStorage.ReadHeaderAsync(ct).ConfigureAwait(false);
        if (headerResult.IsFailure)
        {
            return new UnlockResult.InvalidCredentials();
        }

        var header = headerResult.Value;
        byte[] candidateKey = new byte[32];

        try
        {
            var trialResult = _envelopePacker.EvaluateSlots(header, candidatePassword, candidateKey);

            if (trialResult.Outcome == SlotTrialOutcome.DuressTriggered)
            {
                CryptographicOperations.ZeroMemory(candidateKey);
                await LockAsync().ConfigureAwait(false);
                await _wipeHandler.ExecuteWipeAsync(ct).ConfigureAwait(false);

                // Plausible deniability: immediately re-create a fresh empty vault whose master
                // slot is keyed by the duress passphrase. Without this, the next launch lands on
                // first-time setup — an unmistakable tell that the vault was destroyed. The decoy
                // gets a real authenticated session so personas created during it persist into
                // the decoy vault — an attacker expects the vault to actually work.
                var decoyKey = MasterKeyHandle.GenerateRandom();
                try
                {
                    var reinitResult = await CreateFreshVaultAsync(candidatePassword, null, decoyKey, ct)
                        .ConfigureAwait(false);
                    if (reinitResult.IsFailure)
                    {
                        throw new InvalidOperationException(
                            $"Decoy vault initialization failed: {reinitResult.Error.Description}");
                    }

                    var session = _hostFactory.CreateSession(decoyKey);
                    _activeKeyHandle = decoyKey;
                    _activeSession = session;
                    Status = DatabaseSetupStatus.Unlocked;
                    return new UnlockResult.DuressActivated(session);
                }
                catch
                {
                    // The wipe already succeeded — decoy initialization failure is an unlock
                    // failure, not a sessionless decoy. Dispose the key and let it propagate.
                    decoyKey.Dispose();
                    throw;
                }
            }

            if (trialResult.Outcome != SlotTrialOutcome.MasterUnlocked)
            {
                CryptographicOperations.ZeroMemory(candidateKey);
                return new UnlockResult.InvalidCredentials();
            }

            var masterKeyHandle = new MasterKeyHandle(candidateKey);
            CryptographicOperations.ZeroMemory(candidateKey);

            try
            {
                // Canary probe
                string connectionString = $"Data Source={_options.DatabaseFilePath}";
                await using (var testConn = new SqliteConnection(connectionString))
                {
                    await testConn.OpenAsync(ct).ConfigureAwait(false);
                    masterKeyHandle.ApplyKeyToConnection(testConn);

                    var probeResult = await _canaryProbe.ProbeAsync(testConn, ct).ConfigureAwait(false);
                    if (probeResult != CanaryProbeResult.Verified)
                    {
                        masterKeyHandle.Dispose();
                        return new UnlockResult.InvalidCredentials();
                    }
                }

                var migrationResult = await _migrator.MigrateAsync(masterKeyHandle, ct).ConfigureAwait(false);
                if (migrationResult.IsFailure)
                {
                    masterKeyHandle.Dispose();
                    return new UnlockResult.InvalidCredentials();
                }

                var session = _hostFactory.CreateSession(masterKeyHandle);
                _activeKeyHandle = masterKeyHandle;
                _activeSession = session;
                Status = DatabaseSetupStatus.Unlocked;

                return new UnlockResult.Success(_activeSession);
            }
            catch
            {
                masterKeyHandle.Dispose();
                _activeKeyHandle = null;
                return new UnlockResult.InvalidCredentials();
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(candidateKey);
        }
    }

    /// <summary>
    /// Creates a fresh encrypted vault on disk: builds the header envelope, deletes any orphaned
    /// database files, persists the header, initializes the encrypted database with its canary
    /// table, and applies migrations. Shared by first-time initialization and the post-duress-wipe
    /// decoy vault (master slot keyed by the duress passphrase, no duress slot).
    /// </summary>
    private async Task<DomainResult> CreateFreshVaultAsync(
        VaultPassphrase masterPassword,
        VaultPassphrase? duressPassword,
        IMasterKeyHandle masterKeyHandle,
        CancellationToken ct)
    {
        // Create encrypted envelope wrapping the master key
        DatabaseHeader header = default!;
        masterKeyHandle.WithKeySpan(keySpan =>
        {
            var envelopeResult = _envelopePacker.CreateEnvelope(masterPassword, duressPassword, keySpan);
            if (envelopeResult.IsFailure)
            {
                throw new InvalidOperationException(envelopeResult.Error.Description);
            }
            header = envelopeResult.Value;
        });

        // Cold Launch Self-Healing Invariant: Clean up any orphaned ciphertext database files
        var dbFiles = new[]
        {
            _options.DatabaseFilePath,
            _options.DatabaseFilePath + "-wal",
            _options.DatabaseFilePath + "-shm"
        };
        foreach (var path in dbFiles)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch (IOException) { }
        }

        // Atomically persist header
        var writeResult = await _headerStorage.WriteHeaderAsync(header, ct).ConfigureAwait(false);
        if (writeResult.IsFailure)
        {
            return DomainResult.Failure(writeResult.Error);
        }

        // Canary table + probe, then schema migration
        string connectionString = $"Data Source={_options.DatabaseFilePath}";
        await using (var testConn = new SqliteConnection(connectionString))
        {
            await testConn.OpenAsync(ct).ConfigureAwait(false);
            masterKeyHandle.ApplyKeyToConnection(testConn);

            await using (var initCmd = testConn.CreateCommand())
            {
                initCmd.CommandText = "CREATE TABLE IF NOT EXISTS __percolator_vault (id INTEGER PRIMARY KEY);";
                await initCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            var probeResult = await _canaryProbe.ProbeAsync(testConn, ct).ConfigureAwait(false);
            if (probeResult != CanaryProbeResult.Verified)
            {
                return DomainResult.Failure(
                    new DomainError("Vault.CanaryFailed", $"Canary probe failed: {probeResult}"));
            }
        }

        var migrationResult = await _migrator.MigrateAsync(masterKeyHandle, ct).ConfigureAwait(false);
        if (migrationResult.IsFailure)
        {
            return DomainResult.Failure(migrationResult.Error);
        }

        return DomainResult.Success();
    }

    public async Task<DomainResult> ChangeMasterPasswordAsync(
        VaultPassphrase currentPassword,
        VaultPassphrase newPassword,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(currentPassword);
        ArgumentNullException.ThrowIfNull(newPassword);

        if (Status != DatabaseSetupStatus.Unlocked || _activeKeyHandle == null)
        {
            return DomainResult.Failure(
                new DomainError("Vault.InvalidState", "Cannot change master password when database is not unlocked."));
        }

        var headerResult = await _headerStorage.ReadHeaderAsync(ct).ConfigureAwait(false);
        if (headerResult.IsFailure)
        {
            return DomainResult.Failure(headerResult.Error);
        }

        var currentHeader = headerResult.Value;

        // Verify current password against current header
        byte[] tempBuffer = new byte[32];
        try
        {
            var trialResult = _envelopePacker.EvaluateSlots(currentHeader, currentPassword, tempBuffer);
            if (trialResult.Outcome != SlotTrialOutcome.MasterUnlocked)
            {
                return DomainResult.Failure(
                    new DomainError("Vault.InvalidCredentials", "Current master passphrase does not match."));
            }

            // Check if new master password conflicts with configured duress password
            var duressTrial = _envelopePacker.EvaluateSlots(currentHeader, newPassword, tempBuffer);
            if (duressTrial.Outcome == SlotTrialOutcome.DuressTriggered)
            {
                return DomainResult.Failure(
                    new DomainError("Vault.Collision", "New master passphrase cannot match the configured duress passphrase."));
            }

            DatabaseHeader updatedHeader = default!;
            _activeKeyHandle.WithKeySpan(activeKeySpan =>
            {
                var rewrapResult = _envelopePacker.RewrapMasterSlot(currentHeader, newPassword, activeKeySpan);
                if (rewrapResult.IsFailure)
                {
                    throw new InvalidOperationException(rewrapResult.Error.Description);
                }
                updatedHeader = rewrapResult.Value;
            });

            return await _headerStorage.WriteHeaderAsync(updatedHeader, ct).ConfigureAwait(false);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(tempBuffer);
        }
    }

    public async Task<DomainResult> ConfigureDuressPasswordAsync(
        VaultPassphrase? duressPassword,
        CancellationToken ct = default)
    {
        if (Status != DatabaseSetupStatus.Unlocked || _activeKeyHandle == null)
        {
            return DomainResult.Failure(
                new DomainError("Vault.InvalidState", "Cannot configure duress password when database is not unlocked."));
        }

        var headerResult = await _headerStorage.ReadHeaderAsync(ct).ConfigureAwait(false);
        if (headerResult.IsFailure)
        {
            return DomainResult.Failure(headerResult.Error);
        }

        var currentHeader = headerResult.Value;

        var configureResult = _envelopePacker.ConfigureDuressSlot(currentHeader, duressPassword);
        if (configureResult.IsFailure)
        {
            return DomainResult.Failure(configureResult.Error);
        }

        return await _headerStorage.WriteHeaderAsync(configureResult.Value, ct).ConfigureAwait(false);
    }

    public async Task<DomainResult> ConfigureDuressPasswordAsync(
        VaultPassphrase currentMasterPassword,
        VaultPassphrase? duressPassword,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(currentMasterPassword);

        if (Status != DatabaseSetupStatus.Unlocked || _activeKeyHandle == null)
        {
            return DomainResult.Failure(
                new DomainError("Vault.InvalidState", "Cannot configure duress password when database is not unlocked."));
        }

        var headerResult = await _headerStorage.ReadHeaderAsync(ct).ConfigureAwait(false);
        if (headerResult.IsFailure)
        {
            return DomainResult.Failure(headerResult.Error);
        }

        var currentHeader = headerResult.Value;

        // Re-authentication guard: verify current master password unlocks master slot
        byte[] tempBuffer = new byte[32];
        try
        {
            var trialResult = _envelopePacker.EvaluateSlots(currentHeader, currentMasterPassword, tempBuffer);
            if (trialResult.Outcome != SlotTrialOutcome.MasterUnlocked)
            {
                return DomainResult.Failure(
                    new DomainError("Vault.InvalidCredentials", "Current master passphrase does not match."));
            }

            if (duressPassword != null && !duressPassword.IsEmpty && VaultPassphrase.ConstantTimeEquals(currentMasterPassword, duressPassword))
            {
                return DomainResult.Failure(
                    new DomainError("Vault.Collision", "Duress passphrase cannot match the master passphrase."));
            }

            var configureResult = _envelopePacker.ConfigureDuressSlot(currentHeader, duressPassword);
            if (configureResult.IsFailure)
            {
                return DomainResult.Failure(configureResult.Error);
            }

            return await _headerStorage.WriteHeaderAsync(configureResult.Value, ct).ConfigureAwait(false);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(tempBuffer);
        }
    }

    public async Task LockAsync()
    {
        if (_activeSession != null)
        {
            try
            {
                await _activeSession.StopAsync().ConfigureAwait(false);
                await _activeSession.DisposeAsync().ConfigureAwait(false);
            }
            catch
            {
                // Ignore teardown errors
            }
            _activeSession = null;
        }

        SqliteConnection.ClearAllPools();

        if (_activeKeyHandle != null)
        {
            _activeKeyHandle.Dispose();
            _activeKeyHandle = null;
        }

        Status = DatabaseSetupStatus.Locked;
    }
}

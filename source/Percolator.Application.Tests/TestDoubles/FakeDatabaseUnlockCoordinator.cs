using Percolator.Application2.Ports;
using Percolator.Domain.Common;
using Percolator.Domain.Security.ValueObjects;

namespace Percolator.Application.Tests.TestDoubles;

/// <summary>
/// An in-memory test double implementing <see cref="IDatabaseUnlockCoordinator"/> for fast, deterministic unit testing.
/// </summary>
public sealed class FakeDatabaseUnlockCoordinator : IDatabaseUnlockCoordinator
{
    private readonly IDuressWipeHandler? _wipeHandler;
    private VaultPassphrase? _masterPassword;
    private VaultPassphrase? _duressPassword;
    private FakeAuthenticatedSession? _activeSession;

    public DatabaseSetupStatus Status { get; set; } = DatabaseSetupStatus.Uninitialized;

    public FakeDatabaseUnlockCoordinator(
        IDuressWipeHandler? wipeHandler = null,
        DatabaseSetupStatus initialStatus = DatabaseSetupStatus.Uninitialized)
    {
        _wipeHandler = wipeHandler;
        Status = initialStatus;
    }

    public Task<DomainResult> ConfigureDuressPasswordAsync(
        VaultPassphrase currentMasterPassword,
        VaultPassphrase? duressPassword,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(currentMasterPassword);

        if (Status != DatabaseSetupStatus.Unlocked || _masterPassword == null)
        {
            return Task.FromResult(DomainResult.Failure(
                new DomainError("Vault.InvalidState", "Cannot configure duress password when database is not unlocked.")));
        }

        if (!VaultPassphrase.ConstantTimeEquals(currentMasterPassword, _masterPassword))
        {
            return Task.FromResult(DomainResult.Failure(
                new DomainError("Vault.InvalidCredentials", "Current master passphrase does not match.")));
        }

        return ConfigureDuressPasswordAsync(duressPassword, ct);
    }

    public Task<DatabaseSetupStatus> DetermineStatusAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (_masterPassword == null && Status == DatabaseSetupStatus.Uninitialized)
        {
            return Task.FromResult(DatabaseSetupStatus.Uninitialized);
        }

        return Task.FromResult(Status);
    }

    public Task<DomainResult<IAuthenticatedSession>> InitializeAsync(
        VaultPassphrase masterPassword,
        VaultPassphrase? duressPassword = null,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(masterPassword);

        if (duressPassword != null && !duressPassword.IsEmpty && VaultPassphrase.ConstantTimeEquals(masterPassword, duressPassword))
        {
            return Task.FromResult(DomainResult<IAuthenticatedSession>.Failure(
                new DomainError("Vault.Collision", "Duress passphrase cannot match the master passphrase.")));
        }

        _masterPassword?.Dispose();
        _duressPassword?.Dispose();

        _masterPassword = VaultPassphrase.Create(masterPassword.WithUtf8Span(s => System.Text.Encoding.UTF8.GetString(s)));
        _duressPassword = duressPassword != null && !duressPassword.IsEmpty
            ? VaultPassphrase.Create(duressPassword.WithUtf8Span(s => System.Text.Encoding.UTF8.GetString(s)))
            : null;

        _activeSession = new FakeAuthenticatedSession();
        Status = DatabaseSetupStatus.Unlocked;

        return Task.FromResult(DomainResult<IAuthenticatedSession>.Success(_activeSession));
    }

    public async Task<UnlockResult> UnlockAsync(
        VaultPassphrase candidatePassword,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(candidatePassword);

        if (_masterPassword != null && VaultPassphrase.ConstantTimeEquals(candidatePassword, _masterPassword))
        {
            _activeSession = new FakeAuthenticatedSession();
            Status = DatabaseSetupStatus.Unlocked;
            return new UnlockResult.Success(_activeSession);
        }

        if (_duressPassword != null && VaultPassphrase.ConstantTimeEquals(candidatePassword, _duressPassword))
        {
            if (_wipeHandler != null)
            {
                await _wipeHandler.ExecuteWipeAsync(ct);
            }

            _masterPassword?.Dispose();
            _masterPassword = null;
            _duressPassword?.Dispose();
            _duressPassword = null;

            if (_activeSession != null)
            {
                await _activeSession.DisposeAsync();
                _activeSession = null;
            }

            Status = DatabaseSetupStatus.Uninitialized;
            return new UnlockResult.DuressActivated();
        }

        return new UnlockResult.InvalidCredentials();
    }

    public Task<DomainResult> ChangeMasterPasswordAsync(
        VaultPassphrase currentPassword,
        VaultPassphrase newPassword,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(currentPassword);
        ArgumentNullException.ThrowIfNull(newPassword);

        if (Status != DatabaseSetupStatus.Unlocked || _masterPassword == null)
        {
            return Task.FromResult(DomainResult.Failure(
                new DomainError("Vault.InvalidState", "Cannot change master password when database is not unlocked.")));
        }

        if (!VaultPassphrase.ConstantTimeEquals(currentPassword, _masterPassword))
        {
            return Task.FromResult(DomainResult.Failure(
                new DomainError("Vault.InvalidCredentials", "Invalid current master passphrase.")));
        }

        if (_duressPassword != null && VaultPassphrase.ConstantTimeEquals(newPassword, _duressPassword))
        {
            return Task.FromResult(DomainResult.Failure(
                new DomainError("Vault.Collision", "New master passphrase cannot match the configured duress passphrase.")));
        }

        _masterPassword.Dispose();
        _masterPassword = VaultPassphrase.Create(newPassword.WithUtf8Span(s => System.Text.Encoding.UTF8.GetString(s)));

        return Task.FromResult(DomainResult.Success());
    }

    public Task<DomainResult> ConfigureDuressPasswordAsync(
        VaultPassphrase? duressPassword,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        if (Status != DatabaseSetupStatus.Unlocked || _masterPassword == null)
        {
            return Task.FromResult(DomainResult.Failure(
                new DomainError("Vault.InvalidState", "Cannot configure duress password when database is not unlocked.")));
        }

        if (duressPassword == null || duressPassword.IsEmpty)
        {
            _duressPassword?.Dispose();
            _duressPassword = null;
            return Task.FromResult(DomainResult.Success());
        }

        if (VaultPassphrase.ConstantTimeEquals(_masterPassword, duressPassword))
        {
            return Task.FromResult(DomainResult.Failure(
                new DomainError("Vault.Collision", "Duress passphrase cannot match the master passphrase.")));
        }

        _duressPassword?.Dispose();
        _duressPassword = VaultPassphrase.Create(duressPassword.WithUtf8Span(s => System.Text.Encoding.UTF8.GetString(s)));

        return Task.FromResult(DomainResult.Success());
    }

    public async Task LockAsync()
    {
        if (_activeSession != null)
        {
            await _activeSession.StopAsync();
            await _activeSession.DisposeAsync();
            _activeSession = null;
        }

        Status = DatabaseSetupStatus.Locked;
    }
}

namespace Percolator.Application2.Ports;

using Percolator.Domain.Common;
using Percolator.Domain.Security.ValueObjects;

/// <summary>
/// Primary driving application port for encrypted database lifecycle, master key derivation,
/// session unlocking, envelope re-wrapping, emergency duress wipe coordination, and graceful relocking.
/// </summary>
public interface IDatabaseUnlockCoordinator
{
    /// <summary>
    /// Gets the current setup and security status of the database.
    /// </summary>
    DatabaseSetupStatus Status { get; }

    /// <summary>
    /// Inspects persistent storage for the presence and validity of the header and database files to determine initial setup state.
    /// If percolator.header is missing, always returns <see cref="DatabaseSetupStatus.Uninitialized"/>.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The detected <see cref="DatabaseSetupStatus"/>.</returns>
    Task<DatabaseSetupStatus> DetermineStatusAsync(CancellationToken ct = default);

    /// <summary>
    /// Initializes a brand new encrypted database with the specified master passphrase and optional duress passphrase.
    /// Forcefully unlinks any orphaned database ciphertext files if the header was missing.
    /// Generates salt, derives master key, encrypts slots, verifies canary query, and runs initial migrations.
    /// </summary>
    /// <param name="masterPassword">The master passphrase for encrypting the database key envelope.</param>
    /// <param name="duressPassword">Optional emergency duress passphrase (cannot match master passphrase).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A successful domain result containing the opened <see cref="IAuthenticatedSession"/>, or a failure result.</returns>
    Task<DomainResult<IAuthenticatedSession>> InitializeAsync(
        VaultPassphrase masterPassword,
        VaultPassphrase? duressPassword = null,
        CancellationToken ct = default);

    /// <summary>
    /// Evaluates a candidate passphrase against the master slot and duress slot.
    /// </summary>
    /// <param name="candidatePassword">The candidate passphrase entered by the user.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// <see cref="UnlockResult.Success"/> with the active session if master credentials are valid;
    /// <see cref="UnlockResult.DuressActivated"/> if emergency duress was triggered;
    /// or <see cref="UnlockResult.InvalidCredentials"/> if authentication failed.
    /// </returns>
    Task<UnlockResult> UnlockAsync(
        VaultPassphrase candidatePassword,
        CancellationToken ct = default);

    /// <summary>
    /// Updates the master passphrase within an active authenticated session by re-wrapping the database key in the master slot.
    /// Does not require re-encrypting the underlying SQLite database file (O(1) operation).
    /// </summary>
    /// <param name="currentPassword">The existing master passphrase to verify.</param>
    /// <param name="newPassword">The new master passphrase to encrypt the envelope with.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A successful domain result if the re-wrap succeeded, or a failure result if current credentials were invalid.</returns>
    Task<DomainResult> ChangeMasterPasswordAsync(
        VaultPassphrase currentPassword,
        VaultPassphrase newPassword,
        CancellationToken ct = default);

    /// <summary>
    /// Sets, updates, or clears (if null or empty) the emergency duress passphrase within an active session.
    /// </summary>
    /// <param name="duressPassword">The new duress passphrase to configure, or null/empty to disable duress protection.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A successful domain result if updated, or failure if the duress passphrase matches the master passphrase.</returns>
    Task<DomainResult> ConfigureDuressPasswordAsync(
        VaultPassphrase? duressPassword,
        CancellationToken ct = default);

    /// <summary>
    /// Gracefully stops background workers, flushes connection pools, zeroizes master key memory, and disposes the authenticated host.
    /// Transitions status to <see cref="DatabaseSetupStatus.Locked"/>.
    /// </summary>
    Task LockAsync();
}

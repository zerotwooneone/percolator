using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Percolator.Application.Delivery.Ports;
using Percolator.Application.Ports;
using Percolator.Domain.Common;
using Percolator.Domain.Identities.Ports;
using Percolator.Domain.Identities.ValueObjects;
using Percolator.Domain.Security.Ports;
using Percolator.Domain.Security.ValueObjects;

namespace Percolator.Application.Delivery;

/// <summary>
/// Hosted pre-key maintenance: keeps the relay-hosted bundle healthy by rotating the
/// local signed prekey on a fixed age schedule, pruning retained generations, and
/// topping up one-time prekeys when the relay-hosted pool drops below the low watermark.
/// Evaluations are throttled per drain target and serialized so concurrent OPK-consumed
/// notifications cannot race a rotation/top-up pair.
/// </summary>
public sealed class PreKeyMaintenanceService : IPreKeyMaintenanceService
{
    /// <summary>Hosted OPK pool size at-or-below which a top-up publish runs.</summary>
    public const int LowWatermark = 35;
    /// <summary>Desired hosted OPK pool size after a top-up.</summary>
    public const int TargetPoolSize = 100;
    /// <summary>Minimum age between non-forced status checks for one drain target.</summary>
    public static readonly TimeSpan StatusCheckInterval = TimeSpan.FromHours(12);
    /// <summary>Signed-prekey rotation age.</summary>
    public static readonly TimeSpan SignedPreKeyRotationAge = TimeSpan.FromHours(48);
    /// <summary>Retained generations younger than this are never pruned.</summary>
    public static readonly TimeSpan SignedPreKeyRetentionAge = TimeSpan.FromDays(7);
    /// <summary>Pruning never deletes the newest this-many generations.</summary>
    public const int MinRetainedSignedPreKeys = 3;

    private readonly IPrivatePreKeyStore _preKeyStore;
    private readonly ILocalIdentityKeyStore _identityKeyStore;
    private readonly ICryptoEngine _cryptoEngine;
    private readonly IRelayMailboxProvisioner _provisioner;
    private readonly IRelayDirectory _relayDirectory;
    private readonly IDateTimeProvider _timeProvider;
    private readonly ILogger<PreKeyMaintenanceService> _logger;

    // Serialize evaluations so a forced (post-consumption) run cannot interleave with
    // a throttled one for the same — or another — mailbox; work is rare and short.
    private readonly SemaphoreSlim _evaluationGate = new(1, 1);
    private readonly ConcurrentDictionary<MailboxDrainTarget, DateTimeOffset> _lastChecked = new();

    public PreKeyMaintenanceService(
        IPrivatePreKeyStore preKeyStore,
        ILocalIdentityKeyStore identityKeyStore,
        ICryptoEngine cryptoEngine,
        IRelayMailboxProvisioner provisioner,
        IRelayDirectory relayDirectory,
        IDateTimeProvider timeProvider,
        ILogger<PreKeyMaintenanceService> logger)
    {
        _preKeyStore = preKeyStore ?? throw new ArgumentNullException(nameof(preKeyStore));
        _identityKeyStore = identityKeyStore ?? throw new ArgumentNullException(nameof(identityKeyStore));
        _cryptoEngine = cryptoEngine ?? throw new ArgumentNullException(nameof(cryptoEngine));
        _provisioner = provisioner ?? throw new ArgumentNullException(nameof(provisioner));
        _relayDirectory = relayDirectory ?? throw new ArgumentNullException(nameof(relayDirectory));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public void NotifyOneTimePreKeyConsumed(MailboxDrainTarget target)
    {
        _ = EvaluateSafelyAsync(target);
    }

    /// <inheritdoc />
    public async Task EvaluateAsync(MailboxDrainTarget target, bool forced, CancellationToken ct = default)
    {
        var now = _timeProvider.UtcNow;
        if (!forced &&
            _lastChecked.TryGetValue(target, out var lastCheckedUtc) &&
            now - lastCheckedUtc < StatusCheckInterval)
        {
            return;
        }

        await _evaluationGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Re-check inside the gate: a queued-up evaluation may already have run.
            now = _timeProvider.UtcNow;
            if (!forced &&
                _lastChecked.TryGetValue(target, out lastCheckedUtc) &&
                now - lastCheckedUtc < StatusCheckInterval)
            {
                return;
            }

            // 1. Rotate the local signed prekey on age (or when none exists yet —
            //    the persona bootstrap normally stores one, so null is an edge we
            //    still cover) and prune retained generations.
            var currentSpk = await _preKeyStore
                .GetSignedPreKeyAsync(target.OwnerIdentityId, DeviceId.Primary, ct)
                .ConfigureAwait(false);

            SignedPreKeyId currentKeyId;
            if (currentSpk != null && now - currentSpk.CreatedAtUtc < SignedPreKeyRotationAge)
            {
                currentKeyId = currentSpk.KeyId;
            }
            else
            {
                using var identityPriv = await _identityKeyStore
                    .GetIdentityPrivateKeyAsync(target.OwnerIdentityId, ct)
                    .ConfigureAwait(false);
                if (identityPriv == null)
                {
                    _logger.LogWarning(
                        "Pre-key maintenance skipped for {Owner}: identity private key unavailable.",
                        target.OwnerIdentityId.Value);
                    return;
                }

                var (spkPriv, spkPub) = _cryptoEngine.GenerateEphemeralKeyPair();
                try
                {
                    var signature = DeviceLinkProof.FromSpan(
                        _cryptoEngine.SignEd25519(identityPriv.Span, spkPub.Span));
                    currentKeyId = await _preKeyStore
                        .StoreSignedPreKeyAsync(target.OwnerIdentityId, DeviceId.Primary, spkPriv, spkPub, signature, ct)
                        .ConfigureAwait(false);
                }
                finally
                {
                    spkPriv.Dispose();
                }

                await _preKeyStore
                    .PruneSignedPreKeysAsync(
                        target.OwnerIdentityId,
                        DeviceId.Primary,
                        now - SignedPreKeyRetentionAge,
                        MinRetainedSignedPreKeys,
                        ct)
                    .ConfigureAwait(false);
            }

            // 2. Relay-hosted bundle status (fresh token resolution — never cached).
            var tokensResult = await _relayDirectory
                .GetMailboxTokensAsync(target.OwnerIdentityId, target.RelayIdentityId, ct)
                .ConfigureAwait(false);
            if (tokensResult.IsFailure)
            {
                _logger.LogWarning(
                    "Pre-key maintenance skipped for {Owner}: token resolution failed ({Code}).",
                    target.OwnerIdentityId.Value,
                    tokensResult.Error.Code);
                return;
            }

            var statusResult = await _provisioner
                .GetPreKeyStatusAsync(DeviceId.Primary, target.Endpoint, tokensResult.Value!, ct)
                .ConfigureAwait(false);
            if (statusResult.IsFailure)
            {
                _logger.LogWarning(
                    "Pre-key status check failed for {Owner} on relay {Relay}: {Description}",
                    target.OwnerIdentityId.Value,
                    target.RelayIdentityId.Value,
                    statusResult.Error.Description);
                return;
            }

            var status = statusResult.Value!;

            // 3. Publish when the hosted bundle is missing, OPKs ran low, or the
            //    relay still advertises a superseded signed-prekey generation.
            if (!status.Found ||
                status.OneTimePreKeyCount <= LowWatermark ||
                status.SignedPreKeyId != currentKeyId)
            {
                var toGenerate = status.OneTimePreKeyCount <= LowWatermark
                    ? TargetPoolSize - (int)status.OneTimePreKeyCount
                    : 0;

                var published = await _provisioner
                    .PublishPreKeyBundleAsync(
                        target.OwnerIdentityId,
                        DeviceId.Primary,
                        target.Endpoint,
                        tokensResult.Value!,
                        toGenerate,
                        ct)
                    .ConfigureAwait(false);
                if (published.IsFailure)
                {
                    _logger.LogWarning(
                        "Pre-key bundle publish failed for {Owner} on relay {Relay}: {Description}",
                        target.OwnerIdentityId.Value,
                        target.RelayIdentityId.Value,
                        published.Error.Description);
                }
            }

            // 4. Record after a completed check (status RPC reached the relay) —
            //    failures above leave lastChecked untouched so the next reconnect
            //    or OPK-consumed notification retries promptly.
            _lastChecked[target] = _timeProvider.UtcNow;
        }
        finally
        {
            _evaluationGate.Release();
        }
    }

    // Fire-and-forget path for the drain service: a forced evaluation must never
    // fault the envelope-processing loop, so errors are logged and swallowed.
    private async Task EvaluateSafelyAsync(MailboxDrainTarget target)
    {
        try
        {
            await EvaluateAsync(target, forced: true, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Forced pre-key evaluation failed for {Owner}.", target.OwnerIdentityId.Value);
        }
    }
}

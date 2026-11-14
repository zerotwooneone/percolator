using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Percolator.Application.Ports;
using Percolator.Desktop.Common.Storage;
using Percolator.Desktop.Features.Startup.Models;
using Percolator.Domain.Common;
using Percolator.Domain.Identities.Model;
using Percolator.Domain.Identities.Ports;
using Percolator.Domain.Identities.ValueObjects;
using Percolator.Domain.Security.Ports;
using Percolator.Domain.Security.ValueObjects;
using PublicIdentityId = Percolator.Domain.Identities.PublicIdentityId;

namespace Percolator.Desktop.Common.Services;

/// <summary>
/// Default <see cref="IPersonaDirectory"/>: resolved from the authenticated session
/// container, so the vault's encrypted repositories arrive as constructor dependencies.
/// There is no sessionless path — the decoy vault produced by duress activation is a real
/// vault and is driven through the same session plumbing as any other.
/// </summary>
public sealed class PersonaDirectoryService : IPersonaDirectory
{
    public const int DefaultPreferredPort = 5100;

    private readonly IIdentityProfileRepository _profiles;
    private readonly ILocalIdentityKeyStore _keys;
    private readonly IPrivatePreKeyStore _preKeys;
    private readonly ICryptoEngine _crypto;
    private readonly IDateTimeProvider _time;
    private readonly IDesktopSettingsRepository _settings;

    public PersonaDirectoryService(
        IIdentityProfileRepository profiles,
        ILocalIdentityKeyStore keys,
        IPrivatePreKeyStore preKeys,
        ICryptoEngine crypto,
        IDateTimeProvider time,
        IDesktopSettingsRepository settings)
    {
        _profiles = profiles ?? throw new ArgumentNullException(nameof(profiles));
        _keys = keys ?? throw new ArgumentNullException(nameof(keys));
        _preKeys = preKeys ?? throw new ArgumentNullException(nameof(preKeys));
        _crypto = crypto ?? throw new ArgumentNullException(nameof(crypto));
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
    }

    public async Task<IReadOnlyList<PersonaDirectoryEntry>> ListAsync(
        CancellationToken ct = default)
    {
        var profiles = await _profiles.GetAllAsync(ct).ConfigureAwait(false);
        var settings = (await _settings.GetAllAsync(ct).ConfigureAwait(false))
            .ToDictionary(s => s.IdentityId, s => s.Settings);

        return profiles
            .Where(p => p.Role == IdentityRole.UserPersona)
            .Select(p => ToEntry(p, settings))
            .OrderBy(e => e.SidebarOrder)
            .ThenBy(e => e.DisplayName, StringComparer.Ordinal)
            .ToList();
    }

    public async Task<PersonaDirectoryEntry> CreateAsync(
        PublicIdentityId identityId,
        string displayName,
        int preferredPort,
        CancellationToken ct = default)
    {
        var trimmed = (displayName ?? string.Empty).Trim();
        if (trimmed.Length == 0)
        {
            throw new ArgumentException("Display name cannot be whitespace.", nameof(displayName));
        }

        var profileResult = IdentityProfile.Create(identityId, trimmed, IdentityRole.UserPersona, _time);
        if (profileResult.IsFailure)
        {
            throw new InvalidOperationException(profileResult.Error.Description);
        }

        var (ikPriv, ikPub) = _crypto.GenerateEphemeralKeyPair();
        var (spkPriv, spkPub) = _crypto.GenerateEphemeralKeyPair();
        using var profileKey = ProfileKey.Generate();
        try
        {
            var identityKey = IdentityKey.FromSpan(ikPub.Span);
            await _keys.SaveIdentityKeysAsync(identityId, ikPriv, identityKey, profileKey, ct)
                .ConfigureAwait(false);

            var signature = DeviceLinkProof.FromSpan(_crypto.SignEd25519(ikPriv.Span, spkPub.Span));
            await _preKeys.StoreSignedPreKeyAsync(identityId, DeviceId.Primary, spkPriv, spkPub, signature, ct)
                .ConfigureAwait(false);
        }
        finally
        {
            ikPriv.Zeroize();
            spkPriv.Zeroize();
        }

        await _profiles.SaveAsync(profileResult.Value!, ct).ConfigureAwait(false);

        var existingSettings = await _settings.GetAsync(identityId.Value, ct).ConfigureAwait(false);
        var order = existingSettings?.SidebarOrder
            ?? (await _settings.GetAllAsync(ct).ConfigureAwait(false)).Count;
        var settingsEntity = new IdentityLaunchSettings(preferredPort, null, false, order);
        await _settings.SaveAsync(identityId.Value, settingsEntity, ct).ConfigureAwait(false);

        return new PersonaDirectoryEntry(
            identityId.Value, trimmed, preferredPort, null, false, order);
    }

    public async Task RenameAsync(
        PublicIdentityId identityId,
        string displayName,
        CancellationToken ct = default)
    {
        var trimmed = (displayName ?? string.Empty).Trim();
        if (trimmed.Length == 0)
        {
            throw new ArgumentException("Display name cannot be whitespace.", nameof(displayName));
        }

        var existing = await _profiles.GetByIdAsync(identityId, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Persona no longer exists.");

        // IdentityProfile has no rename member; rehydrate preserves id/role/state/dates.
        var renamed = IdentityProfile.Rehydrate(
            existing.Id, trimmed, existing.Role, existing.State,
            existing.CreatedAtUtc, existing.DisabledAtUtc);
        await _profiles.SaveAsync(renamed, ct).ConfigureAwait(false);
    }

    public async Task ShredAndDeleteAsync(
        PublicIdentityId identityId,
        CancellationToken ct = default)
    {
        await _keys.DeleteIdentityKeysAsync(identityId, ct).ConfigureAwait(false);
        await _profiles.DeleteAsync(identityId, ct).ConfigureAwait(false);
        await _settings.DeleteAsync(identityId.Value, ct).ConfigureAwait(false);
    }

    private PersonaDirectoryEntry ToEntry(
        IdentityProfile profile,
        IReadOnlyDictionary<PublicIdentityId, IdentityLaunchSettings> settings)
    {
        var index = profile.Id.Value;
        var entity = settings.TryGetValue(profile.Id, out var s) ? s : null;
        return new PersonaDirectoryEntry(
            index,
            profile.DisplayName,
            entity?.PreferredPort ?? DefaultPreferredPort,
            entity?.LastBoundPort,
            entity?.AutoStart ?? false,
            entity?.SidebarOrder ?? int.MaxValue);
    }
}

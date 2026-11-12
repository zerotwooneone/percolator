using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Percolator.Application.Ports;
using Percolator.Application2.Ports;
using Percolator.Desktop.Common.Storage;
using Percolator.Desktop.Features.Startup.Models;
using Percolator.Domain.Common;
using Percolator.Domain.Identities.Model;
using Percolator.Domain.Identities.Ports;
using Percolator.Domain.Identities.ValueObjects;
using Percolator.Domain.Security.Ports;
using Percolator.Infrastructure.Persistence.Repositories;

namespace Percolator.Desktop.Common.Services;

/// <summary>
/// Default <see cref="IPersonaDirectory"/>: opens a DI scope on the authenticated session
/// and reads/writes personas through the vault's encrypted repositories. There is no
/// sessionless path — the decoy vault produced by duress activation is a real vault and
/// is driven through the same session plumbing as any other.
/// </summary>
public sealed class PersonaDirectoryService : IPersonaDirectory
{
    public const int DefaultPreferredPort = 5100;

    private readonly IDesktopSettingsRepository _settings;
    private readonly ICryptoEngine _crypto;
    private readonly IDateTimeProvider _time;

    public PersonaDirectoryService(
        IDesktopSettingsRepository settings,
        ICryptoEngine crypto,
        IDateTimeProvider time)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _crypto = crypto ?? throw new ArgumentNullException(nameof(crypto));
        _time = time ?? throw new ArgumentNullException(nameof(time));
    }

    public async Task<IReadOnlyList<PersonaDirectoryEntry>> ListAsync(
        IAuthenticatedSession session, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(session);

        using var scope = CreateScope(session);
        var profiles = await scope.ServiceProvider
            .GetRequiredService<IIdentityProfileRepository>()
            .GetAllAsync(ct)
            .ConfigureAwait(false);
        var settings = (await _settings.GetAllAsync(ct).ConfigureAwait(false))
            .ToDictionary(s => s.IdentityId);

        return profiles
            .Select(p => ToEntry(p, settings))
            .OrderBy(e => e.SidebarOrder)
            .ThenBy(e => e.DisplayName, StringComparer.Ordinal)
            .ToList();
    }

    public async Task<PersonaDirectoryEntry> CreateAsync(
        IAuthenticatedSession session,
        PublicIdentityId identityId,
        string displayName,
        int preferredPort,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(session);

        var trimmed = (displayName ?? string.Empty).Trim();
        if (trimmed.Length == 0)
        {
            throw new ArgumentException("Display name cannot be whitespace.", nameof(displayName));
        }

        using var scope = CreateScope(session);
        var profiles = scope.ServiceProvider.GetRequiredService<IIdentityProfileRepository>();
        var keys = scope.ServiceProvider.GetRequiredService<ILocalIdentityKeyStore>();

        var profileResult = IdentityProfile.Create(identityId, trimmed, IdentityRole.UserPersona, _time);
        if (profileResult.IsFailure)
        {
            throw new InvalidOperationException(profileResult.Error.Description);
        }

        var (privateKey, publicKey) = _crypto.GenerateEphemeralKeyPair();
        try
        {
            var identityKey = IdentityKey.FromSpan(publicKey.Span);
            await SaveKeyAsync(keys, identityId, privateKey.Span.ToArray(), identityKey, ct)
                .ConfigureAwait(false);
        }
        finally
        {
            privateKey.Dispose();
        }

        await profiles.SaveAsync(profileResult.Value!, ct).ConfigureAwait(false);

        var existingSettings = await _settings.GetAsync(identityId.Value, ct).ConfigureAwait(false);
        var order = existingSettings?.SidebarOrder
            ?? (await _settings.GetAllAsync(ct).ConfigureAwait(false)).Count;
        var settingsEntity = new DesktopIdentitySettingsEntity(
            identityId.Value, preferredPort, null, false, order);
        await _settings.SaveAsync(settingsEntity, ct).ConfigureAwait(false);

        return new PersonaDirectoryEntry(
            identityId.Value, trimmed, preferredPort, null, false, order);
    }

    public async Task RenameAsync(
        IAuthenticatedSession session,
        PublicIdentityId identityId,
        string displayName,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(session);

        var trimmed = (displayName ?? string.Empty).Trim();
        if (trimmed.Length == 0)
        {
            throw new ArgumentException("Display name cannot be whitespace.", nameof(displayName));
        }

        using var scope = CreateScope(session);
        var profiles = scope.ServiceProvider.GetRequiredService<IIdentityProfileRepository>();
        var existing = await profiles.GetByIdAsync(identityId, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Persona no longer exists.");

        // IdentityProfile has no rename member; rehydrate preserves id/role/state/dates.
        var renamed = IdentityProfile.Rehydrate(
            existing.Id, trimmed, existing.Role, existing.State,
            existing.CreatedAtUtc, existing.DisabledAtUtc);
        await profiles.SaveAsync(renamed, ct).ConfigureAwait(false);
    }

    public async Task ShredAndDeleteAsync(
        IAuthenticatedSession session,
        PublicIdentityId identityId,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(session);

        using var scope = CreateScope(session);
        var keys = scope.ServiceProvider.GetRequiredService<ILocalIdentityKeyStore>();
        var profiles = scope.ServiceProvider.GetRequiredService<IIdentityProfileRepository>();

        await DeleteKeyAsync(keys, identityId, ct).ConfigureAwait(false);
        await profiles.DeleteAsync(identityId, ct).ConfigureAwait(false);
        await _settings.DeleteAsync(identityId.Value, ct).ConfigureAwait(false);
    }

    private static IServiceScope CreateScope(IAuthenticatedSession session) =>
        session.Services.GetRequiredService<IServiceScopeFactory>().CreateScope();

    private static Task SaveKeyAsync(
        ILocalIdentityKeyStore store,
        PublicIdentityId id,
        byte[] privateKey,
        IdentityKey publicKey,
        CancellationToken ct) => store switch
        {
            SqliteLocalIdentityKeyStore sqlite => sqlite.SaveKeyAsync(id, privateKey, publicKey, ct),
            _ => throw new NotSupportedException($"Key store {store.GetType().Name} cannot persist keys.")
        };

    private static Task DeleteKeyAsync(
        ILocalIdentityKeyStore store,
        PublicIdentityId id,
        CancellationToken ct) => store switch
        {
            SqliteLocalIdentityKeyStore sqlite => sqlite.DeleteKeyAsync(id, ct),
            _ => Task.CompletedTask
        };

    private PersonaDirectoryEntry ToEntry(
        IdentityProfile profile,
        Dictionary<Guid, DesktopIdentitySettingsEntity> settings)
    {
        var index = profile.Id.Value;
        var entity = settings.TryGetValue(index, out var s) ? s : null;
        return new PersonaDirectoryEntry(
            index,
            profile.DisplayName,
            entity?.PreferredPort ?? DefaultPreferredPort,
            entity?.LastBoundPort,
            entity?.AutoStart ?? false,
            entity?.SidebarOrder ?? int.MaxValue);
    }
}

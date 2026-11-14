namespace Percolator.Infrastructure.Persistence.Repositories;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Percolator.Application.Ports;
using Percolator.Infrastructure.Persistence.Entities;
using PublicIdentityId = Percolator.Domain.Identities.PublicIdentityId;

/// <summary>
/// Singleton <see cref="IVaultSettingsStore"/> over
/// <see cref="IDbContextFactory{PercolatorDbContext}"/> — every call opens a short-lived
/// context against the encrypted vault. Port values are range-validated at this boundary
/// (1–65535); out-of-range values throw <see cref="ArgumentOutOfRangeException"/>.
/// </summary>
public sealed class SqliteVaultSettingsStore : IVaultSettingsStore
{
    private const int MinPort = 1;
    private const int MaxPort = 65535;

    private readonly IDbContextFactory<PercolatorDbContext> _contextFactory;

    public SqliteVaultSettingsStore(IDbContextFactory<PercolatorDbContext> contextFactory)
    {
        _contextFactory = contextFactory ?? throw new ArgumentNullException(nameof(contextFactory));
    }

    public async Task<IdentityLaunchSettings?> GetAsync(PublicIdentityId identityId, CancellationToken ct = default)
    {
        var key = identityId.Value.ToByteArray();
        await using var db = await _contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var entity = await db.IdentitySettings
            .AsNoTracking()
            .FirstOrDefaultAsync(e => e.IdentityId == key, ct)
            .ConfigureAwait(false);
        return entity is null ? null : Map(entity);
    }

    public async Task<IReadOnlyList<(PublicIdentityId IdentityId, IdentityLaunchSettings Settings)>> GetAllAsync(
        CancellationToken ct = default)
    {
        await using var db = await _contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var entities = await db.IdentitySettings
            .AsNoTracking()
            .OrderBy(e => e.SidebarOrder)
            .ToListAsync(ct)
            .ConfigureAwait(false);
        return entities
            .Select(e => (new PublicIdentityId(new Guid(e.IdentityId)), Map(e)))
            .ToList();
    }

    public async Task SaveAsync(
        PublicIdentityId identityId,
        IdentityLaunchSettings settings,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ValidatePort(nameof(settings.PreferredPort), settings.PreferredPort);
        if (settings.LastBoundPort is { } lastBound)
        {
            ValidatePort(nameof(settings.LastBoundPort), lastBound);
        }

        var key = identityId.Value.ToByteArray();
        await using var db = await _contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var entity = await db.IdentitySettings
            .FirstOrDefaultAsync(e => e.IdentityId == key, ct)
            .ConfigureAwait(false);
        if (entity is null)
        {
            entity = new IdentitySettingsEntity { IdentityId = key };
            db.IdentitySettings.Add(entity);
        }

        entity.PreferredPort = settings.PreferredPort;
        entity.LastBoundPort = settings.LastBoundPort;
        entity.AutoStart = settings.AutoStart;
        entity.SidebarOrder = settings.SidebarOrder;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task DeleteAsync(PublicIdentityId identityId, CancellationToken ct = default)
    {
        var key = identityId.Value.ToByteArray();
        await using var db = await _contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        await db.IdentitySettings
            .Where(e => e.IdentityId == key)
            .ExecuteDeleteAsync(ct)
            .ConfigureAwait(false);
    }

    public async Task<string?> GetAppSettingAsync(string key, CancellationToken ct = default)
    {
        await using var db = await _contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var entity = await db.AppSettings
            .AsNoTracking()
            .FirstOrDefaultAsync(e => e.SettingKey == key, ct)
            .ConfigureAwait(false);
        return entity?.SettingValue;
    }

    public async Task SetAppSettingAsync(string key, string value, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(value);

        await using var db = await _contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var entity = await db.AppSettings
            .FirstOrDefaultAsync(e => e.SettingKey == key, ct)
            .ConfigureAwait(false);
        if (entity is null)
        {
            entity = new AppSettingEntity { SettingKey = key };
            db.AppSettings.Add(entity);
        }

        entity.SettingValue = value;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    private static IdentityLaunchSettings Map(IdentitySettingsEntity entity) =>
        new(entity.PreferredPort, entity.LastBoundPort, entity.AutoStart, entity.SidebarOrder);

    private static void ValidatePort(string paramName, int port)
    {
        if (port < MinPort || port > MaxPort)
        {
            throw new ArgumentOutOfRangeException(paramName, port, $"Port must be between {MinPort} and {MaxPort}.");
        }
    }
}

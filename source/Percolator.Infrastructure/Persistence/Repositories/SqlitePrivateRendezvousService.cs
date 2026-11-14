namespace Percolator.Infrastructure.Persistence.Repositories;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Percolator.Apps.Discovery.Features.PrivateRendezvous;
using Percolator.Domain.Common;
using Percolator.Domain.Identities.ValueObjects;
using Percolator.Infrastructure.Persistence;
using Percolator.Infrastructure.Persistence.Entities;

public sealed class SqlitePrivateRendezvousService : IPrivateRendezvousService
{
    public const int DefaultMaxEntries = 10_000;
    public const int MaxQueryLocators = 100;
    public static readonly TimeSpan DefaultEntryTtl = TimeSpan.FromHours(48);

    private readonly IDbContextFactory<PercolatorDbContext> _contextFactory;
    private readonly IDateTimeProvider? _clock;
    private readonly TimeSpan _entryTtl;
    private readonly int _maxEntries;

    public SqlitePrivateRendezvousService(
        IDbContextFactory<PercolatorDbContext> contextFactory,
        IDateTimeProvider? clock = null,
        TimeSpan? entryTtl = null,
        int maxEntries = DefaultMaxEntries)
    {
        _contextFactory = contextFactory ?? throw new ArgumentNullException(nameof(contextFactory));
        _clock = clock;
        _entryTtl = entryTtl ?? DefaultEntryTtl;
        _maxEntries = maxEntries > 0 ? maxEntries : DefaultMaxEntries;
    }

    public BlindedLocator DeriveLocator(
        Domain.Identities.PublicIdentityId peerId,
        ReadOnlySpan<byte> sharedSecret,
        DiscoveryEpoch epoch)
    {
        return BlindedLocator.Derive(peerId, sharedSecret, epoch);
    }

    public BlindedLocator DeriveLocator(
        Domain.Identities.PublicIdentityId peerId,
        RendezvousSharedSecret sharedSecret,
        DiscoveryEpoch epoch) =>
        DeriveLocator(peerId, sharedSecret.Span, epoch);

    public IReadOnlyList<BlindedLocator> DeriveLocatorWindow(
        Domain.Identities.PublicIdentityId peerId,
        ReadOnlySpan<byte> sharedSecret,
        DiscoveryEpoch currentEpoch)
    {
        return BlindedLocator.DeriveWindow(peerId, sharedSecret, currentEpoch);
    }

    public IReadOnlyList<BlindedLocator> DeriveLocatorWindow(
        Domain.Identities.PublicIdentityId peerId,
        RendezvousSharedSecret sharedSecret,
        DiscoveryEpoch currentEpoch) =>
        DeriveLocatorWindow(peerId, sharedSecret.Span, currentEpoch);

    public async ValueTask<DomainResult> PublishEncryptedRendezvousAsync(
        BlindedLocator locator,
        EncryptedRendezvousRoute encryptedRoute,
        CancellationToken ct = default)
    {
        if (!locator.IsValid)
        {
            return DomainResult.Failure(new DomainError(
                "INVALID_LOCATOR", "Blinded locator cannot be empty or invalid."));
        }

        if (!encryptedRoute.IsValid)
        {
            return DomainResult.Failure(new DomainError(
                "INVALID_ENCRYPTED_ROUTE", "Encrypted rendezvous route is invalid."));
        }

        await using var db = await _contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        DateTimeOffset nowUtc = _clock?.UtcNow ?? DateTimeOffset.UtcNow;
        byte[] locBytes = locator.Span.ToArray();
        var existing = await db.RelayRendezvousEntries
            .FirstOrDefaultAsync(e => e.BlindedLocator == locBytes, ct)
            .ConfigureAwait(false);

        if (existing == null)
        {
            int currentCount = await db.RelayRendezvousEntries.CountAsync(ct).ConfigureAwait(false);
            if (currentCount >= _maxEntries)
            {
                return DomainResult.Failure(new DomainError(
                    "RENDEZVOUS_CAPACITY_EXCEEDED", "Relay rendezvous table capacity has been reached."));
            }

            var entity = new RelayRendezvousEntryEntity
            {
                BlindedLocator = locBytes,
                EncryptedRoute = encryptedRoute.Span.ToArray(),
                RouteDescriptor = null,
                PublishedAtUtc = nowUtc
            };

            await db.RelayRendezvousEntries.AddAsync(entity, ct).ConfigureAwait(false);
        }
        else
        {
            existing.EncryptedRoute = encryptedRoute.Span.ToArray();
            existing.RouteDescriptor = null;
            existing.PublishedAtUtc = nowUtc;
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return DomainResult.Success();
    }

    public async ValueTask<DomainResult> PublishRendezvousAsync(
        BlindedLocator locator,
        string relayRouteDescriptor,
        CancellationToken ct = default)
    {
        if (!locator.IsValid)
        {
            return DomainResult.Failure(new DomainError(
                "INVALID_LOCATOR", "Blinded locator cannot be empty."));
        }

        var routeCheck = RelayRouteDescriptor.Create(relayRouteDescriptor);
        if (routeCheck.IsFailure)
        {
            return DomainResult.Failure(routeCheck.Error);
        }

        await using var db = await _contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        DateTimeOffset nowUtc = _clock?.UtcNow ?? DateTimeOffset.UtcNow;
        byte[] locBytes = locator.Span.ToArray();
        var existing = await db.RelayRendezvousEntries
            .FirstOrDefaultAsync(e => e.BlindedLocator == locBytes, ct)
            .ConfigureAwait(false);

        if (existing == null)
        {
            int currentCount = await db.RelayRendezvousEntries.CountAsync(ct).ConfigureAwait(false);
            if (currentCount >= _maxEntries)
            {
                return DomainResult.Failure(new DomainError(
                    "RENDEZVOUS_CAPACITY_EXCEEDED", "Relay rendezvous table capacity has been reached."));
            }

            var entity = new RelayRendezvousEntryEntity
            {
                BlindedLocator = locBytes,
                EncryptedRoute = null,
                RouteDescriptor = routeCheck.Value.Value,
                PublishedAtUtc = nowUtc
            };

            await db.RelayRendezvousEntries.AddAsync(entity, ct).ConfigureAwait(false);
        }
        else
        {
            existing.EncryptedRoute = null;
            existing.RouteDescriptor = routeCheck.Value.Value;
            existing.PublishedAtUtc = nowUtc;
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return DomainResult.Success();
    }

    public async ValueTask<IReadOnlyList<BlindedRendezvousMatch>> ResolveRendezvousAsync(
        IReadOnlyList<BlindedLocator> locators,
        CancellationToken ct = default)
    {
        if (locators == null || locators.Count == 0)
        {
            return Array.Empty<BlindedRendezvousMatch>();
        }

        await using var db = await _contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        DateTimeOffset nowUtc = _clock?.UtcNow ?? DateTimeOffset.UtcNow;
        int maxToInspect = Math.Min(locators.Count, MaxQueryLocators);
        DateTimeOffset minPublished = nowUtc - _entryTtl;
        var matches = new List<BlindedRendezvousMatch>();

        for (int i = 0; i < maxToInspect; i++)
        {
            BlindedLocator loc = locators[i];
            if (!loc.IsValid)
            {
                continue;
            }

            byte[] locBytes = loc.Span.ToArray();
            var entry = await db.RelayRendezvousEntries
                .AsNoTracking()
                .FirstOrDefaultAsync(e => e.BlindedLocator == locBytes && e.PublishedAtUtc > minPublished, ct)
                .ConfigureAwait(false);

            if (entry != null)
            {
                if (entry.EncryptedRoute != null && entry.EncryptedRoute.Length > 0)
                {
                    var routeResult = EncryptedRendezvousRoute.FromBytes(entry.EncryptedRoute);
                    if (routeResult.IsSuccess)
                    {
                        matches.Add(new BlindedRendezvousMatch(loc, routeResult.Value));
                    }
                }
                else if (!string.IsNullOrEmpty(entry.RouteDescriptor))
                {
                    matches.Add(new BlindedRendezvousMatch(loc, entry.RouteDescriptor));
                }
            }
        }

        return matches;
    }
}

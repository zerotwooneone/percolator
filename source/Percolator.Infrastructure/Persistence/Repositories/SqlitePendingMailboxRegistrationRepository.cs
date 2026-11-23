namespace Percolator.Infrastructure.Persistence.Repositories;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Percolator.Domain.Delivery.Hosting;
using Percolator.Domain.Delivery.Ports;
using Percolator.Domain.Delivery.ValueObjects;
using Percolator.Domain.Identities;
using Percolator.Infrastructure.Persistence;
using Percolator.Infrastructure.Persistence.Entities;

public sealed class SqlitePendingMailboxRegistrationRepository : IPendingMailboxRegistrationRepository
{
    private readonly IDbContextFactory<PercolatorDbContext> _contextFactory;

    public SqlitePendingMailboxRegistrationRepository(IDbContextFactory<PercolatorDbContext> contextFactory)
    {
        _contextFactory = contextFactory ?? throw new ArgumentNullException(nameof(contextFactory));
    }

    public async Task<PendingMailboxRegistration?> GetByApplicantRoutingTokenAsync(
        BlindedRoutingToken applicantRoutingToken,
        CancellationToken ct = default)
    {
        var routingBytes = applicantRoutingToken.ToByteArray();
        await using var db = await _contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var entity = await db.PendingMailboxRegistrations
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.ApplicantRoutingToken == routingBytes, ct)
            .ConfigureAwait(false);

        return entity != null ? MapEntityToDomain(entity) : null;
    }

    public async Task<IReadOnlyList<PendingMailboxRegistration>> GetPendingByRelayIdAsync(
        PublicIdentityId relayIdentityId,
        CancellationToken ct = default)
    {
        string relayIdStr = relayIdentityId.Value.ToString();
        int pendingStatus = (int)RegistrationModerationStatus.PendingApproval;

        await using var db = await _contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var entities = await db.PendingMailboxRegistrations
            .AsNoTracking()
            .Where(p => p.RelayId == relayIdStr && p.Status == pendingStatus)
            .OrderBy(p => p.RequestedAtUtc)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return entities.Select(MapEntityToDomain).ToList();
    }

    public async Task SaveAsync(
        PendingMailboxRegistration registration,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(registration);
        string relayIdStr = registration.RelayIdentityId.Value.ToString();
        var routingBytes = registration.ApplicantRoutingToken.ToByteArray();

        await using var db = await _contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var existing = await db.PendingMailboxRegistrations
            .FirstOrDefaultAsync(p => p.RelayId == relayIdStr && p.ApplicantRoutingToken == routingBytes, ct)
            .ConfigureAwait(false);

        if (existing == null)
        {
            var entity = new PendingMailboxRegistrationEntity
            {
                RelayId = relayIdStr,
                ApplicantRoutingToken = routingBytes,
                Status = (int)registration.Status,
                RequestedAtUtc = registration.RequestedAtUtc,
                DecidedAtUtc = registration.DecidedAtUtc
            };
            await db.PendingMailboxRegistrations.AddAsync(entity, ct).ConfigureAwait(false);
        }
        else
        {
            existing.Status = (int)registration.Status;
            existing.DecidedAtUtc = registration.DecidedAtUtc;
        }

        try
        {
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        catch (DbUpdateException)
        {
            await using var retryDb = await _contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
            var reloaded = await retryDb.PendingMailboxRegistrations
                .FirstOrDefaultAsync(p => p.RelayId == relayIdStr && p.ApplicantRoutingToken == routingBytes, ct)
                .ConfigureAwait(false);
            if (reloaded != null)
            {
                reloaded.Status = (int)registration.Status;
                reloaded.DecidedAtUtc = registration.DecidedAtUtc;
                await retryDb.SaveChangesAsync(ct).ConfigureAwait(false);
            }
        }
    }

    public async Task UpdateStatusAsync(
        PublicIdentityId relayIdentityId,
        BlindedRoutingToken applicantRoutingToken,
        RegistrationModerationStatus status,
        CancellationToken ct = default)
    {
        string relayIdStr = relayIdentityId.Value.ToString();
        var routingBytes = applicantRoutingToken.ToByteArray();

        await using var db = await _contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var existing = await db.PendingMailboxRegistrations
            .FirstOrDefaultAsync(p => p.RelayId == relayIdStr && p.ApplicantRoutingToken == routingBytes, ct)
            .ConfigureAwait(false);

        if (existing != null)
        {
            existing.Status = (int)status;
            existing.DecidedAtUtc = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
    }

    private static PendingMailboxRegistration MapEntityToDomain(PendingMailboxRegistrationEntity entity)
    {
        Guid relayGuid = Guid.TryParse(entity.RelayId, out var g) ? g : Guid.Empty;
        var relayId = new PublicIdentityId(relayGuid);
        var token = BlindedRoutingToken.FromBytes(entity.ApplicantRoutingToken);

        return new PendingMailboxRegistration(
            relayId,
            token,
            (RegistrationModerationStatus)entity.Status,
            entity.RequestedAtUtc,
            entity.DecidedAtUtc);
    }
}

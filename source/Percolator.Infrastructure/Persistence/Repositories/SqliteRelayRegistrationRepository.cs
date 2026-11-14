namespace Percolator.Infrastructure.Persistence.Repositories;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Percolator.Domain.Delivery.Model;
using Percolator.Domain.Delivery.Ports;
using Percolator.Domain.Delivery.ValueObjects;
using Percolator.Domain.Identities.ValueObjects;
using Percolator.Infrastructure.Persistence.Entities;

public sealed class SqliteRelayRegistrationRepository : IRelayRegistrationRepository
{
    private readonly PercolatorDbContext _dbContext;

    public SqliteRelayRegistrationRepository(PercolatorDbContext dbContext)
    {
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
    }

    public async Task<RelayRegistration?> GetByIdAsync(
        Domain.Identities.PublicIdentityId relayIdentityId,
        CancellationToken ct = default)
    {
        var entity = await _dbContext.RelayRegistrations
            .AsNoTracking()
            .FirstOrDefaultAsync(r => r.RelayIdentityId == relayIdentityId.Value, ct)
            .ConfigureAwait(false);

        return entity == null ? null : await MapAsync(entity, ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<RelayRegistration>> GetAllAsync(CancellationToken ct = default)
    {
        var entities = await _dbContext.RelayRegistrations
            .AsNoTracking()
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var registrations = new List<RelayRegistration>(entities.Count);
        foreach (var entity in entities)
        {
            registrations.Add(await MapAsync(entity, ct).ConfigureAwait(false));
        }
        return registrations;
    }

    public async Task<IReadOnlyList<RelayRegistration>> GetByOwnerIdentityAsync(
        Domain.Identities.PublicIdentityId ownerIdentityId,
        CancellationToken ct = default)
    {
        var relayIds = await _dbContext.RelayMailboxBindings
            .Where(b => b.OwnerIdentityId == ownerIdentityId.Value)
            .Select(b => b.RelayIdentityId)
            .Distinct()
            .ToListAsync(ct)
            .ConfigureAwait(false);

        if (relayIds.Count == 0)
        {
            return Array.Empty<RelayRegistration>();
        }

        var entities = await _dbContext.RelayRegistrations
            .AsNoTracking()
            .Where(r => relayIds.Contains(r.RelayIdentityId))
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var registrations = new List<RelayRegistration>(entities.Count);
        foreach (var entity in entities)
        {
            registrations.Add(await MapAsync(entity, ct).ConfigureAwait(false));
        }
        return registrations;
    }

    public async Task SaveAsync(RelayRegistration registration, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(registration);

        var entity = await _dbContext.RelayRegistrations
            .FirstOrDefaultAsync(r => r.RelayIdentityId == registration.Id.Value, ct)
            .ConfigureAwait(false);

        if (entity == null)
        {
            entity = new RelayRegistrationEntity { RelayIdentityId = registration.Id.Value };
            _dbContext.RelayRegistrations.Add(entity);
        }

        entity.Endpoint = registration.Endpoint.Value;
        entity.PinnedCertificateSha256 = registration.PinnedCertificate?.Span.ToArray();
        entity.CreatedAtUtc = registration.CreatedAtUtc;

        var existing = await _dbContext.RelayMailboxBindings
            .Where(b => b.RelayIdentityId == registration.Id.Value)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var currentOwners = registration.Bindings.Select(b => b.OwnerIdentityId.Value).ToHashSet();
        foreach (var stale in existing.Where(b => !currentOwners.Contains(b.OwnerIdentityId)))
        {
            _dbContext.RelayMailboxBindings.Remove(stale);
        }

        foreach (var binding in registration.Bindings)
        {
            var row = existing.FirstOrDefault(b => b.OwnerIdentityId == binding.OwnerIdentityId.Value);
            if (row == null)
            {
                _dbContext.RelayMailboxBindings.Add(new RelayMailboxBindingEntity
                {
                    RelayIdentityId = registration.Id.Value,
                    OwnerIdentityId = binding.OwnerIdentityId.Value,
                    RoutingToken = binding.RoutingToken.ToByteArray(),
                    DrainToken = binding.DrainToken.Span.ToArray(),
                    PreviousDrainToken = binding.PreviousDrainToken?.Span.ToArray(),
                    LinkedAtUtc = binding.LinkedAtUtc
                });
            }
            else
            {
                row.RoutingToken = binding.RoutingToken.ToByteArray();
                row.DrainToken = binding.DrainToken.Span.ToArray();
                row.PreviousDrainToken = binding.PreviousDrainToken?.Span.ToArray();
                row.LinkedAtUtc = binding.LinkedAtUtc;
            }
        }

        await _dbContext.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task DeleteAsync(Domain.Identities.PublicIdentityId relayIdentityId, CancellationToken ct = default)
    {
        await _dbContext.RelayRegistrations
            .Where(r => r.RelayIdentityId == relayIdentityId.Value)
            .ExecuteDeleteAsync(ct)
            .ConfigureAwait(false);
    }

    private async Task<RelayRegistration> MapAsync(RelayRegistrationEntity entity, CancellationToken ct)
    {
        var bindingRows = await _dbContext.RelayMailboxBindings
            .AsNoTracking()
            .Where(b => b.RelayIdentityId == entity.RelayIdentityId)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var bindings = bindingRows.Select(b => new RelayMailboxBinding(
            new Domain.Identities.PublicIdentityId(b.OwnerIdentityId),
            BlindedRoutingToken.FromBytes(b.RoutingToken),
            DeliveryToken.FromSpan(b.DrainToken),
            b.LinkedAtUtc,
            b.PreviousDrainToken == null ? null : DeliveryToken.FromSpan(b.PreviousDrainToken)));

        return RelayRegistration.Rehydrate(
            new Domain.Identities.PublicIdentityId(entity.RelayIdentityId),
            RelayEndpoint.Create(entity.Endpoint).Value!,
            entity.PinnedCertificateSha256 == null ? null : CertificatePin.FromSpan(entity.PinnedCertificateSha256),
            entity.CreatedAtUtc,
            bindings);
    }
}

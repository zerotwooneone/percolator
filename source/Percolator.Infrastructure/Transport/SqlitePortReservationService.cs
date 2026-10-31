namespace Percolator.Infrastructure.Transport;

using System;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Percolator.Application.Ports;
using Percolator.Domain.Common;
using Percolator.Infrastructure.Persistence;
using Percolator.Infrastructure.Persistence.Entities;

public sealed class SqlitePortReservationService : IPortReservationService
{
    private readonly IDbContextFactory<PercolatorDbContext> _contextFactory;
    private readonly IDateTimeProvider _timeProvider;
    private readonly Func<int, bool> _socketProber;

    public SqlitePortReservationService(
        IDbContextFactory<PercolatorDbContext> contextFactory,
        IDateTimeProvider timeProvider,
        Func<int, bool>? socketProber = null)
    {
        _contextFactory = contextFactory ?? throw new ArgumentNullException(nameof(contextFactory));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _socketProber = socketProber ?? DefaultProbePort;
    }

    public async Task<DomainResult<PortLease>> AcquirePortLeaseAsync(PortRange range, CancellationToken ct = default)
    {
        if (range.Start.Value > range.End.Value || range.Start.Value <= 0 || range.End.Value > 65535)
        {
            return DomainResult<PortLease>.Failure(new DomainError("INVALID_PORT_RANGE", "Invalid port range specified."));
        }

        await using var db = await _contextFactory.CreateDbContextAsync(ct);
        DateTimeOffset now = _timeProvider.UtcNow;

        var activeLeasedPorts = await db.OpsReservedPorts
            .Where(p => p.LeaseExpiryUtc > now)
            .Select(p => p.PortNumber)
            .ToListAsync(ct);

        var activePortSet = activeLeasedPorts.ToHashSet();

        for (int port = range.Start.Value; port <= range.End.Value; port++)
        {
            if (activePortSet.Contains(port))
            {
                continue;
            }

            if (!_socketProber(port))
            {
                continue;
            }

            var leaseToken = Guid.NewGuid();
            var expiry = now.AddMinutes(30);

            var existingEntity = await db.OpsReservedPorts.FindAsync(new object[] { port }, ct);
            if (existingEntity != null)
            {
                existingEntity.LeaseToken = leaseToken;
                existingEntity.AllocatedAtUtc = now;
                existingEntity.LeaseExpiryUtc = expiry;
            }
            else
            {
                db.OpsReservedPorts.Add(new OpsReservedPortEntity
                {
                    PortNumber = port,
                    LeaseToken = leaseToken,
                    AllocatedAtUtc = now,
                    LeaseExpiryUtc = expiry
                });
            }

            await db.SaveChangesAsync(ct);

            return DomainResult<PortLease>.Success(new PortLease(
                new PortNumber(port),
                new PortLeaseToken(leaseToken),
                expiry));
        }

        return DomainResult<PortLease>.Failure(new DomainError("PORT_UNAVAILABLE", "No available ports in requested range."));
    }

    public async Task<DomainResult> RenewPortLeaseAsync(PortNumber port, PortLeaseToken leaseToken, CancellationToken ct = default)
    {
        await using var db = await _contextFactory.CreateDbContextAsync(ct);
        var entity = await db.OpsReservedPorts.FindAsync(new object[] { port.Value }, ct);

        if (entity == null || entity.LeaseToken != leaseToken.Value)
        {
            return DomainResult.Failure(new DomainError("INVALID_LEASE", "Lease not found or token mismatch."));
        }

        entity.LeaseExpiryUtc = _timeProvider.UtcNow.AddMinutes(30);
        await db.SaveChangesAsync(ct);

        return DomainResult.Success();
    }

    public async Task<DomainResult> ReleasePortLeaseAsync(PortNumber port, PortLeaseToken leaseToken, CancellationToken ct = default)
    {
        await using var db = await _contextFactory.CreateDbContextAsync(ct);
        var entity = await db.OpsReservedPorts.FindAsync(new object[] { port.Value }, ct);

        if (entity != null && entity.LeaseToken == leaseToken.Value)
        {
            db.OpsReservedPorts.Remove(entity);
            await db.SaveChangesAsync(ct);
        }

        return DomainResult.Success();
    }

    public async Task<DomainResult> PurgeExpiredLeasesAsync(CancellationToken ct = default)
    {
        await using var db = await _contextFactory.CreateDbContextAsync(ct);
        DateTimeOffset now = _timeProvider.UtcNow;

        var expired = await db.OpsReservedPorts
            .Where(p => p.LeaseExpiryUtc <= now)
            .ToListAsync(ct);

        if (expired.Count > 0)
        {
            db.OpsReservedPorts.RemoveRange(expired);
            await db.SaveChangesAsync(ct);
        }

        return DomainResult.Success();
    }

    private static bool DefaultProbePort(int port)
    {
        try
        {
            var listener = new TcpListener(IPAddress.IPv6Any, port);
            listener.Server.DualMode = true;
            if (OperatingSystem.IsWindows())
            {
                listener.Server.ExclusiveAddressUse = true;
            }
            listener.Start();
            listener.Stop();
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }
}

namespace Percolator.Application.Ports;

using System;
using System.Threading;
using System.Threading.Tasks;
using Percolator.Domain.Common;

public readonly record struct PortNumber(int Value);
public readonly record struct PortLeaseToken(Guid Value);
public readonly record struct PortRange(PortNumber Start, PortNumber End);
public sealed record PortLease(PortNumber Port, PortLeaseToken LeaseToken, DateTimeOffset ExpiresAtUtc);

public interface IPortReservationService
{
    Task<DomainResult<PortLease>> AcquirePortLeaseAsync(PortRange range, CancellationToken ct = default);
    Task<DomainResult> RenewPortLeaseAsync(PortNumber port, PortLeaseToken leaseToken, CancellationToken ct = default);
    Task<DomainResult> ReleasePortLeaseAsync(PortNumber port, PortLeaseToken leaseToken, CancellationToken ct = default);
    Task<DomainResult> PurgeExpiredLeasesAsync(CancellationToken ct = default);
}

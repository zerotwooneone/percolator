// SPECIFICATION GOVERNANCE: Read Percolator.Desktop/spec/ViewModel.spec.md before modifying this file.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Percolator.Application.Ports;
using Percolator.Domain.Identities;
using Percolator.Infrastructure.Persistence.Repositories;

namespace Percolator.Desktop.Features.Transports.Services;

/// <summary>
/// Singleton facade for <see cref="IPeerContactQueryService"/> in the Desktop session container.
/// Executes queries within an isolated service scope to prevent captive-dependency leaks
/// when called from singleton or root-level consumers.
/// </summary>
public sealed class DesktopPeerContactQueryService : IPeerContactQueryService
{
    private readonly IServiceScopeFactory _scopeFactory;

    public DesktopPeerContactQueryService(IServiceScopeFactory scopeFactory)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
    }

    public async Task<IReadOnlyList<PeerContactSummaryReadModel>> GetContactSummariesAsync(
        PublicIdentityId ownerId, CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var inner = scope.ServiceProvider.GetService<SqlPeerContactQueryService>()
            ?? scope.ServiceProvider.GetServices<IPeerContactQueryService>().FirstOrDefault(s => s is not DesktopPeerContactQueryService);

        if (inner == null)
        {
            return Array.Empty<PeerContactSummaryReadModel>();
        }

        return await inner.GetContactSummariesAsync(ownerId, ct).ConfigureAwait(false);
    }

    public async Task<PeerContactDetailReadModel?> GetContactDetailAsync(
        PublicIdentityId ownerId, PublicIdentityId contactId, CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var inner = scope.ServiceProvider.GetService<SqlPeerContactQueryService>()
            ?? scope.ServiceProvider.GetServices<IPeerContactQueryService>().FirstOrDefault(s => s is not DesktopPeerContactQueryService);

        if (inner == null)
        {
            return null;
        }

        return await inner.GetContactDetailAsync(ownerId, contactId, ct).ConfigureAwait(false);
    }
}

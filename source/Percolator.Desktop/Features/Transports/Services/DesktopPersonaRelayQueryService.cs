// SPECIFICATION GOVERNANCE: Read Percolator.Desktop/spec/UiStateService.spec.md before modifying this file.

namespace Percolator.Desktop.Features.Transports.Services;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Percolator.Application.Delivery.Ports;
using Percolator.Domain.Identities;
using Percolator.Infrastructure.Persistence.Queries;

/// <summary>
/// Singleton facade for <see cref="IPersonaRelayQueryService"/> in the Desktop session container.
/// Executes queries within an isolated service scope to prevent captive-dependency leaks
/// when called from singleton or root-level consumers.
/// </summary>
public sealed class DesktopPersonaRelayQueryService : IPersonaRelayQueryService
{
    private readonly IServiceScopeFactory _scopeFactory;

    public DesktopPersonaRelayQueryService(IServiceScopeFactory scopeFactory)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
    }

    public async Task<IReadOnlyList<PersonaRelayLinkReadModel>> GetRelayLinksForPersonaAsync(
        PublicIdentityId personaId, CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var inner = scope.ServiceProvider.GetService<SqlPersonaRelayQueryService>()
            ?? scope.ServiceProvider.GetServices<IPersonaRelayQueryService>().FirstOrDefault(s => s is not DesktopPersonaRelayQueryService);

        if (inner == null)
        {
            return Array.Empty<PersonaRelayLinkReadModel>();
        }

        return await inner.GetRelayLinksForPersonaAsync(personaId, ct).ConfigureAwait(false);
    }
}

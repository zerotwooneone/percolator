namespace Percolator.Desktop.Features.Transports.Services;

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Percolator.Application.Connections;
using Percolator.Domain.Common;
using Percolator.Domain.Identities;

/// <summary>
/// Singleton facade for <see cref="IInboundConnectionWorkflow"/> in the Desktop session container.
/// Executes workflow operations within an isolated service scope to prevent captive-dependency leaks
/// when called from singleton UI state services or root-level consumers.
/// </summary>
public sealed class DesktopInboundConnectionWorkflow : IInboundConnectionWorkflow
{
    private readonly IServiceScopeFactory _scopeFactory;

    public DesktopInboundConnectionWorkflow(IServiceScopeFactory scopeFactory)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
    }

    public async Task<DomainResult<bool>> AcceptRequestAsync(
        PublicIdentityId personaId,
        PublicIdentityId requesterId,
        CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var inner = scope.ServiceProvider.GetService<InboundConnectionWorkflow>()
            ?? scope.ServiceProvider.GetServices<IInboundConnectionWorkflow>().FirstOrDefault(s => s is not DesktopInboundConnectionWorkflow);

        if (inner == null)
        {
            return DomainResult<bool>.Failure(new DomainError("WORKFLOW_UNAVAILABLE", "InboundConnectionWorkflow is not registered in scope."));
        }

        return await inner.AcceptRequestAsync(personaId, requesterId, ct).ConfigureAwait(false);
    }

    public async Task<DomainResult<bool>> RejectRequestAsync(
        PublicIdentityId personaId,
        PublicIdentityId requesterId,
        CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var inner = scope.ServiceProvider.GetService<InboundConnectionWorkflow>()
            ?? scope.ServiceProvider.GetServices<IInboundConnectionWorkflow>().FirstOrDefault(s => s is not DesktopInboundConnectionWorkflow);

        if (inner == null)
        {
            return DomainResult<bool>.Failure(new DomainError("WORKFLOW_UNAVAILABLE", "InboundConnectionWorkflow is not registered in scope."));
        }

        return await inner.RejectRequestAsync(personaId, requesterId, ct).ConfigureAwait(false);
    }

    public async Task<DomainResult<bool>> BlockRequestAsync(
        PublicIdentityId personaId,
        PublicIdentityId requesterId,
        CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var inner = scope.ServiceProvider.GetService<InboundConnectionWorkflow>()
            ?? scope.ServiceProvider.GetServices<IInboundConnectionWorkflow>().FirstOrDefault(s => s is not DesktopInboundConnectionWorkflow);

        if (inner == null)
        {
            return DomainResult<bool>.Failure(new DomainError("WORKFLOW_UNAVAILABLE", "InboundConnectionWorkflow is not registered in scope."));
        }

        return await inner.BlockRequestAsync(personaId, requesterId, ct).ConfigureAwait(false);
    }
}

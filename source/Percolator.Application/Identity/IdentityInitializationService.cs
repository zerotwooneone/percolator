using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;

namespace Percolator.Application.Identity;

public class IdentityInitializationService : IHostedService
{
    private readonly IIdentityOrchestrator _identityOrchestrator;
    private readonly IdentityConfiguration _identityConfiguration;

    public IdentityInitializationService(IIdentityOrchestrator identityOrchestrator, IdentityConfiguration identityConfiguration)
    {
        _identityOrchestrator = identityOrchestrator;
        _identityConfiguration = identityConfiguration;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        return _identityOrchestrator.LoadOrCreateIdentityAsync(_identityConfiguration.IdentityName, cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }
}

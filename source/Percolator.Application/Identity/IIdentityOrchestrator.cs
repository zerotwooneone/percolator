namespace Percolator.Application.Identity;

public interface IIdentityOrchestrator
{
    Task LoadOrCreateIdentityAsync(string identityName, CancellationToken cancellationToken);
}

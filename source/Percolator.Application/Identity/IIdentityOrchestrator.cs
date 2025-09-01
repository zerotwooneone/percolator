namespace Percolator.Application.Identity;

public interface IIdentityOrchestrator
{
    Task ResolveIdentityAsync(string identityName, CancellationToken cancellationToken, string? fallbackIdentityName=null);
}

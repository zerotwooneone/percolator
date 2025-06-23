namespace Percolator.Application.Identity;

using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// Orchestrates the loading of the active identity into the application context.
/// </summary>
public interface IIdentityOrchestrator
{
    /// <summary>
    /// Loads the specified identity into the ActiveIdentityContext.
    /// </summary>
    /// <param name="identityName">The name of the identity to load.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    Task LoadActiveIdentityAsync(string identityName, CancellationToken cancellationToken=default);
}

using System.Threading;
using System.Threading.Tasks;

namespace Percolator.Application.Identity;

public interface IIdentityOrchestrator
{
    Task LoadOrCreateIdentityAsync(string identityName, CancellationToken cancellationToken);
}

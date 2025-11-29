using System.Threading;
using System.Threading.Tasks;
using Percolator.Identity.Model;

namespace Desktop.Wpf.Features.Self;

public interface IStartupIdentityService
{
    Task<SelfIdentity> ResolveOrCreateAsync(CancellationToken ct = default);
}

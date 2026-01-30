using System.Threading;
using System.Threading.Tasks;

namespace Percolator.Application.Network;

public interface IAdvertisedHostLookup
{
    Task<string> GetAdvertisedHostAsync(CancellationToken ct = default);
}

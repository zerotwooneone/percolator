using Percolator.Application.Identity;
using Percolator.Network;

namespace Percolator.Application.Network;

public class IdentityProvider : IIdentityProvider
{
    private readonly ActiveIdentityContext _activeIdentityContext;

    public IdentityProvider(ActiveIdentityContext activeIdentityContext)
    {
        _activeIdentityContext = activeIdentityContext;
    }

    public string GetThumbprint()
    {
        return _activeIdentityContext.KeyThumbprints[ActiveIdentityContext.IdentityKey];
    }
}

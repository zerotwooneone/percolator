using Percolator.Application.Ports;

namespace Percolator.Infrastructure.Vault;

internal sealed class AuthenticatedSessionHolder
{
    public IAuthenticatedSession? Session { get; set; }
}

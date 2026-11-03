namespace Percolator.Infrastructure.Vault;

using Percolator.Application2.Ports;

internal sealed class AuthenticatedSessionHolder
{
    public IAuthenticatedSession? Session { get; set; }
}

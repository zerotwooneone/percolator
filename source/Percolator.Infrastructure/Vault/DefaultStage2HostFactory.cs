namespace Percolator.Infrastructure.Vault;

using Microsoft.Extensions.Hosting;
using Percolator.Domain.Security.Ports;
using Percolator.Infrastructure.Persistence;

internal sealed class DefaultStage2HostFactory : IStage2HostFactory
{
    private readonly Stage2HostFactory _stage2Factory;

    public DefaultStage2HostFactory(VaultStorageOptions options)
    {
        _stage2Factory = new Stage2HostFactory(options ?? throw new ArgumentNullException(nameof(options)));
    }

    public IHost BuildAuthenticatedHost(IMasterKeyHandle masterKeyHandle)
    {
        return _stage2Factory.BuildAuthenticatedHost(masterKeyHandle);
    }
}

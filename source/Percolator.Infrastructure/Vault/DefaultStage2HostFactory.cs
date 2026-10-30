namespace Percolator.Infrastructure.Vault;

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Percolator.Domain.Security.Ports;
using Percolator.Infrastructure.Persistence;

internal sealed class DefaultStage2HostFactory : IStage2HostFactory
{
    private readonly BaselineStage2HostFactory _baselineFactory;

    public DefaultStage2HostFactory(IOptions<VaultStorageOptions>? options = null)
    {
        _baselineFactory = new BaselineStage2HostFactory(options);
    }

    public IHost BuildAuthenticatedHost(IMasterKeyHandle masterKeyHandle)
    {
        return _baselineFactory.BuildAuthenticatedHost(masterKeyHandle);
    }
}

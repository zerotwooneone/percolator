namespace Percolator.Infrastructure.Vault;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Percolator.Domain.Security.Ports;

internal sealed class DefaultStage2HostFactory : IStage2HostFactory
{
    public IHost BuildAuthenticatedHost(IMasterKeyHandle masterKeyHandle)
    {
        var builder = Host.CreateDefaultBuilder();
        builder.ConfigureServices((_, services) =>
        {
            services.AddSingleton(masterKeyHandle);
        });
        return builder.Build();
    }
}

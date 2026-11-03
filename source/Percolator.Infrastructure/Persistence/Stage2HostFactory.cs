namespace Percolator.Infrastructure.Persistence;

using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Percolator.Domain.Security.Ports;
using Percolator.Infrastructure.DependencyInjection;
using Percolator.Infrastructure.Vault;

public sealed class Stage2HostFactory : IStage2HostFactory
{
    private readonly VaultStorageOptions _options;

    public Stage2HostFactory(IOptions<VaultStorageOptions>? options = null)
    {
        _options = options?.Value ?? new VaultStorageOptions();
    }

    public IHost BuildAuthenticatedHost(IMasterKeyHandle masterKeyHandle)
    {
        ArgumentNullException.ThrowIfNull(masterKeyHandle);

        var builder = Host.CreateDefaultBuilder();
        builder.UseDefaultServiceProvider((_, spOptions) =>
        {
            spOptions.ValidateScopes = true;
            spOptions.ValidateOnBuild = true;
        });

        builder.ConfigureServices((_, services) =>
        {
            services.AddAuthenticatedInfrastructure(masterKeyHandle, _options);
        });

        var host = builder.Build();
        var session = new AuthenticatedSession(host);
        var holder = host.Services.GetService<AuthenticatedSessionHolder>();
        if (holder != null)
        {
            holder.Session = session;
        }

        return host;
    }
}

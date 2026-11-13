namespace Percolator.Infrastructure.Persistence;

using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Percolator.Domain.Security.Ports;
using Percolator.Infrastructure.DependencyInjection;
using Percolator.Infrastructure.Vault;

public sealed class Stage2HostFactory : IStage2HostFactory
{
    private readonly VaultStorageOptions _options;

    public Stage2HostFactory(VaultStorageOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
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

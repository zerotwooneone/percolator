namespace Percolator.Infrastructure.Persistence;

using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Percolator.Application.Ports;
using Percolator.Domain.Security.Ports;
using Percolator.Infrastructure.DependencyInjection;
using Percolator.Infrastructure.Vault;

/// <summary>
/// Infra-side <see cref="ISessionHostFactory"/> — composes the session host purely from
/// Infra slices. The real Desktop session composition lives in DesktopStage2SessionFactory;
/// this exists for Infra tests and any host that doesn't add executable session services.
/// </summary>
public sealed class Stage2HostFactory : ISessionHostFactory
{
    private readonly VaultStorageOptions _options;

    public Stage2HostFactory(VaultStorageOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public IAuthenticatedSession CreateSession(IMasterKeyHandle masterKey)
    {
        return new AuthenticatedSession(BuildAuthenticatedHost(masterKey));
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

        return builder.Build();
    }
}

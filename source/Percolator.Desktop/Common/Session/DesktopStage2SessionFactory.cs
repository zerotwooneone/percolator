namespace Percolator.Desktop.Common.Session;

using System;
using Microsoft.Extensions.Hosting;
using Percolator.Application.Ports;
using Percolator.Domain.Security.Ports;
using Percolator.Infrastructure.DependencyInjection;
using Percolator.Infrastructure.Vault;

/// <summary>
/// Desktop-owned <see cref="ISessionHostFactory"/> (CTX-02). The Stage-2 session host is
/// composed here — in the executable — from the Infrastructure registration slices plus
/// <see cref="DesktopSessionServiceCollectionExtensions.AddDesktopSessionServices"/>,
/// which forwards the Stage-1 services post-unlock code needs. Returns the session
/// wrapping the built host; <see cref="IDatabaseUnlockCoordinator"/> never sees an IHost.
/// </summary>
public sealed class DesktopStage2SessionFactory : ISessionHostFactory
{
    private readonly IServiceProvider _stage1;
    private readonly VaultStorageOptions _options;

    public DesktopStage2SessionFactory(IServiceProvider stage1, VaultStorageOptions options)
    {
        _stage1 = stage1 ?? throw new ArgumentNullException(nameof(stage1));
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public IAuthenticatedSession CreateSession(IMasterKeyHandle masterKey)
    {
        ArgumentNullException.ThrowIfNull(masterKey);

        var builder = Host.CreateDefaultBuilder();
        builder.UseDefaultServiceProvider((_, spOptions) =>
        {
            spOptions.ValidateScopes = true;
            spOptions.ValidateOnBuild = true;
        });

        builder.ConfigureServices((_, services) =>
        {
            services.AddSessionCoreInfrastructure(masterKey, _options);
            services.AddVaultPersistenceInfrastructure();
            services.AddTransportInfrastructure();
            services.AddSessionLifecycleInfrastructure();
            services.AddChatInfrastructure();
            services.AddDiscoveryInfrastructure();
            services.AddBlobStorageInfrastructure();
            services.AddFileTransferInfrastructure();

            services.AddDesktopSessionServices(_stage1);
        });

        var host = builder.Build();
        return new AuthenticatedSession(host);
    }
}

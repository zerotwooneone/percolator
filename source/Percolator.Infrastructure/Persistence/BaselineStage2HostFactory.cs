namespace Percolator.Infrastructure.Persistence;

using System;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Percolator.Application2.Delivery.Ports;
using Percolator.Application2.Ports;
using Percolator.Domain.Channels.Ports;
using Percolator.Domain.Delivery.Ports;
using Percolator.Domain.Identities.Ports;
using Percolator.Domain.Security.Ports;
using Percolator.Infrastructure.Persistence.Repositories;
using Percolator.Infrastructure.Vault;
using Percolator.PluginSdk;

public sealed class BaselineStage2HostFactory : IStage2HostFactory
{
    private readonly VaultStorageOptions _options;

    public BaselineStage2HostFactory(IOptions<VaultStorageOptions>? options = null)
    {
        _options = options?.Value ?? new VaultStorageOptions();
    }

    public IHost BuildAuthenticatedHost(IMasterKeyHandle masterKeyHandle)
    {
        ArgumentNullException.ThrowIfNull(masterKeyHandle);

        var builder = Host.CreateDefaultBuilder();
        builder.ConfigureServices((_, services) =>
        {
            services.AddSingleton(masterKeyHandle);
            services.AddSingleton<DbConnectionInterceptor, SqlCipherPragmaInterceptor>();

            services.AddDbContextFactory<PercolatorDbContext>((sp, dbOptions) =>
            {
                var interceptor = sp.GetRequiredService<DbConnectionInterceptor>();
                var keyHandle = sp.GetRequiredService<IMasterKeyHandle>();
                dbOptions.UseSqlite($"Data Source={_options.DatabaseFilePath};Mode=ReadWriteCreate", sqliteOptions =>
                {
                })
                .AddInterceptors(interceptor)
                .ReplaceService<IModelCacheKeyFactory, PercolatorModelCacheKeyFactory>();
            });

            services.AddScoped(sp =>
            {
                var factory = sp.GetRequiredService<IDbContextFactory<PercolatorDbContext>>();
                var keyHandle = sp.GetRequiredService<IMasterKeyHandle>();
                return new PercolatorDbContext(
                    sp.GetRequiredService<DbContextOptions<PercolatorDbContext>>(),
                    keyHandle);
            });

            services.AddScoped<IDatabaseMigrator, SqliteDatabaseMigrator>();

            // Phase 2 Repositories & Query Services
            services.AddScoped<IIdentityProfileRepository, SqliteIdentityProfileRepository>();
            services.AddScoped<ILocalIdentityKeyStore, SqliteLocalIdentityKeyStore>();
            services.AddScoped<IPrivatePreKeyStore, SqlitePrivatePreKeyStore>();
            services.AddScoped<IRatchetSessionRepository, SqliteRatchetSessionRepository>();
            services.AddScoped<IGroupSenderKeyRepository, SqliteGroupSenderKeyRepository>();
            services.AddScoped<IGroupReceiverSessionRepository, SqliteGroupReceiverSessionRepository>();
            services.AddScoped<IGroupCredentialsRepository, SqliteGroupCredentialsRepository>();
            services.AddScoped<IPendingHandshakeRepository, SqlitePendingHandshakeRepository>();

            services.AddScoped<IOutboxRepository, SqliteOutboxRepository>();
            services.AddScoped<IOutboxQueryService, SqlOutboxQueryService>();

            services.AddScoped<IPeerContactRepository, SqlitePeerContactRepository>();
            services.AddScoped<IPeerContactQueryService, SqlPeerContactQueryService>();
            services.AddScoped<IChannelRepository, SqliteChannelRepository>();

            services.AddScoped<IRelayPreKeyRepository, SqliteRelayPreKeyRepository>();
            services.AddScoped<IRelayProfileRepository, SqliteRelayProfileRepository>();
            services.AddScoped<IRelayLedgerRepository, SqliteRelayLedgerRepository>();
            services.AddScoped<IPeerProfileRepository, SqlitePeerProfileRepository>();
            services.AddScoped<IPeerMailboxRoutingRepository, SqlitePeerMailboxRoutingRepository>();
        });

        return builder.Build();
    }
}

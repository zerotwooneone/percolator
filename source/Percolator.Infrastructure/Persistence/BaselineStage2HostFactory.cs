namespace Percolator.Infrastructure.Persistence;

using System;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Percolator.Application2.Delivery.Ports;
using Percolator.Application2.Ports;
using Percolator.Domain.Channels.Ports;
using Percolator.Domain.Delivery.Ports;
using Percolator.Domain.Identities.Ports;
using Percolator.Domain.Security.Ports;
using Percolator.Infrastructure.Events;
using Percolator.Infrastructure.Maintenance;
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

            // Event Bus
            services.AddSingleton<InMemoryEventBus>();
            services.AddSingleton<IEventBus>(sp => sp.GetRequiredService<InMemoryEventBus>());
            services.AddSingleton<IEventPublisher>(sp => sp.GetRequiredService<InMemoryEventBus>());
            services.AddSingleton<IEventSubscriber>(sp => sp.GetRequiredService<InMemoryEventBus>());

            services.AddDbContextFactory<PercolatorDbContext>((sp, dbOptions) =>
            {
                var interceptor = sp.GetRequiredService<DbConnectionInterceptor>();
                dbOptions.UseSqlite($"Data Source={_options.DatabaseFilePath};Mode=ReadWriteCreate")
                    .AddInterceptors(interceptor);
            });

            services.AddScoped(sp =>
            {
                var factory = sp.GetRequiredService<IDbContextFactory<PercolatorDbContext>>();
                return factory.CreateDbContext();
            });

            services.AddScoped<IDatabaseMigrator, SqliteDatabaseMigrator>();

            // Repositories & Query Services
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

            services.AddScoped(sp => new SqliteRelayPreKeyRepository(sp.GetRequiredService<PercolatorDbContext>()));
            services.AddScoped<IRelayPreKeyRepository>(sp => sp.GetRequiredService<SqliteRelayPreKeyRepository>());
            services.AddScoped(sp => new SqliteRelayProfileRepository(sp.GetRequiredService<PercolatorDbContext>()));
            services.AddScoped<IRelayProfileRepository>(sp => sp.GetRequiredService<SqliteRelayProfileRepository>());
            services.AddScoped(sp => new SqliteRelayLedgerRepository(sp.GetRequiredService<PercolatorDbContext>()));
            services.AddScoped<IRelayLedgerRepository>(sp => sp.GetRequiredService<SqliteRelayLedgerRepository>());
            services.AddScoped<IPeerProfileRepository, SqlitePeerProfileRepository>();
            services.AddScoped<IPeerMailboxRoutingRepository, SqlitePeerMailboxRoutingRepository>();

            // Background Maintenance Pruners
            services.AddHostedService<OutboxRetentionPruner>();
            services.AddHostedService<RelayPreKeyDirectoryPruner>();
            services.AddHostedService<RelayProfileSlotPruner>();
        });

        return builder.Build();
    }
}

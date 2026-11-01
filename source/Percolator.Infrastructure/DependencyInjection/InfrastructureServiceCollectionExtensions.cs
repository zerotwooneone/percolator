namespace Percolator.Infrastructure.DependencyInjection;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Percolator.Application2.Ports;
using Percolator.Apps.Chat.Features.GroupInvitations;
using Percolator.Apps.Chat.Features.Messaging;
using Percolator.Apps.Chat.Ingress;
using Percolator.Domain.Common;
using Percolator.Infrastructure.Common;
using Percolator.Infrastructure.Persistence.Repositories;
using Percolator.Infrastructure.Transport.Workers;
using Percolator.Infrastructure.Vault;

public static class InfrastructureServiceCollectionExtensions
{
    public static IServiceCollection AddBootstrapInfrastructure(
        this IServiceCollection services,
        IConfiguration? configuration = null)
    {
        SQLitePCL.Batteries_V2.Init();

        services.AddSingleton<VaultStorageOptions>();
        services.AddSingleton<IDateTimeProvider, SystemDateTimeProvider>();
        services.AddSingleton<IHeaderStorage, ProtobufHeaderStorage>();
        services.AddSingleton<IDuressWipeHandler, DuressWipeHandler>();
        services.AddSingleton<IVaultEnvelopePacker, VaultEnvelopePacker>();
        services.AddSingleton<IDatabaseCanaryProbe, DatabaseCanaryProbe>();
        services.AddSingleton<IDatabaseMigrator, NoOpDatabaseMigrator>();
        services.AddSingleton<IStage2HostFactory, DefaultStage2HostFactory>();
        services.AddSingleton<IDatabaseUnlockCoordinator, SqlCipherDatabaseUnlockCoordinator>();

        return services;
    }

    public static IServiceCollection AddChatInfrastructure(this IServiceCollection services)
    {
        services.AddScoped<IChatMessageRepository, SqlChatMessageRepository>();
        services.AddScoped<IChatMessageQueryService, SqlChatMessageQueryService>();
        services.AddScoped<IConversationListQueryService, SqlConversationListQueryService>();
        services.AddScoped<IGroupInvitationRepository, SqlGroupInvitationRepository>();
        services.AddScoped<IGroupInvitationQueryService, SqlGroupInvitationQueryService>();
        services.AddScoped<IUnknownGroupMessageCacheRepository, SqlUnknownGroupMessageCacheRepository>();

        services.AddHostedService<DisappearingMessageEvictionWorker>();
        services.AddHostedService<UnknownGroupMessageCachePruner>();
        services.AddHostedService<AbandonedInvitePruner>();

        return services;
    }
}

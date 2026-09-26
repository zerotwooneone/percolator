using Microsoft.Extensions.DependencyInjection;
using Percolator.Application.Chat;
using Percolator.Chat;
using Percolator.Chat.GroupLedger;
using Percolator.Chat.Messaging.App;
using Percolator.Infrastructure.Chat.Persistence;
using Percolator.Infrastructure.Chat.Queries;
using Percolator.Infrastructure.Network;
using Percolator.Infrastructure.Network.Egress;
using Percolator.Infrastructure.Network.RelayHost;
using Percolator.Infrastructure.Outbox;
using Percolator.Infrastructure.Persistence;

namespace Percolator.Infrastructure.Chat;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddChatInfrastructure(this IServiceCollection services)
    {
        services.AddScoped<IDirectConversationRepository, SqliteDirectConversationRepository>();
        services.AddScoped<IGroupConversationRepository, SqliteGroupConversationRepository>();
        services.AddScoped<IMessageRepository, SqliteMessageRepository>();
        services.AddScoped<IDirectConversationResolver, ChatConversationResolver>();
        services.AddScoped<IChatMessageWriter, SqliteChatMessageWriter>();
        services.AddScoped<IGroupCryptoStateRepository, SqliteGroupCryptoStateRepository>();
        services.AddScoped<IPendingGroupInvitationRepository, SqlitePendingGroupInvitationRepository>();
        services.AddScoped<IPendingGroupInvitationQueries, SqlitePendingGroupInvitationQueries>();
        services.AddScoped<IConversationMessageQueries, SqliteConversationMessageQueries>();
        services.AddScoped<IGroupConversationQueries, SqliteGroupConversationQueries>();

        // Register Chunk 4 Relay Ledger services
        services.AddScoped<IRelayGroupLedgerRepository, SqliteRelayGroupLedgerRepository>();
        services.AddScoped<IRelayGroupQueries, SqliteRelayGroupQueries>();
        services.AddScoped<ISelfIdentityQueries, Percolator.Infrastructure.Identity.SelfIdentityQueries>();

        // Register Chunk 4 Relay Live Dispatcher and Egress
        services.AddScoped<IRelayLiveDispatcher, GrpcRelayLiveDispatcher>();
        services.AddScoped<IRelayEgressJobRepository, SqliteRelayEgressJobRepository>();

        // Register Chunk 5 Outbox Dispatcher
        services.AddHostedService<OutboxDispatcherWorker>();

        // Register Chunk 5.6 Delivery Certificate services
        services.AddScoped<IDeliveryCertificateStore, SqliteDeliveryCertificateStore>();
        services.AddScoped<IDeliveryCertificateQueries, SqliteDeliveryCertificateQueries>();

        // Register Chunk 6.3 Self Certificate Service
        services.AddScoped<ISelfCertificateService, SelfCertificateService>();

        // Register LocalIdentitySigner
        services.AddScoped<Percolator.Application.Chat.ILocalIdentitySigner, Percolator.Infrastructure.Chat.LocalIdentitySigner>();

        // Register GroupInviteHandler
        services.AddScoped<Percolator.Application.Chat.IGroupInviteHandler, Percolator.Application.Chat.GroupInviteHandler>();

        // Register Chunk 6 Relay Group Stream Worker
        services.AddHostedService<RelayGroupStreamWorker>();

        return services;
    }
}

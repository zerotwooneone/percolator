using Microsoft.Extensions.DependencyInjection;
using Percolator.Application.KeyExchange;
using Percolator.Application.Network;
using Percolator.Application.Sessions;
using Percolator.Identity;
using Percolator.Sessions;

namespace Percolator.Application
{
    public static class ServiceCollectionExtensions
    {
        public static IServiceCollection AddApplicationServices(this IServiceCollection services)
        {
            // Register new persistence interfaces and their in-memory implementations
            services.AddSingleton<IDoubleRatchetSessionStore, InMemoryDoubleRatchetSessionStore>();
            services.AddSingleton<IConversationStore, InMemoryConversationStore>();

            // Register orchestrators and managers
            services.AddSingleton<X3DHOrchestrator>();
            services.AddSingleton<DirectSessionManager>();

            // Register network transport services
            services.AddSingleton<IMessageTransportService, GrpcMessageTransportService>();
            services.AddSingleton<PercolatorMessageService>(); // gRPC service implementation

            // Leverage existing registrations from domain projects
            // Assuming these are already registered in their respective ServiceCollectionExtensions
            // or will be registered by the Node project.
            // For now, explicitly adding them if they are direct dependencies of the new services.
            services.AddSingleton<Percolator.Identity.IIdentityService, Percolator.Identity.PersistentIdentityService>(); // Explicitly use Identity.IIdentityService
            services.AddSingleton<Percolator.Cryptography.X3DHManager>();
            services.AddSingleton<Percolator.Cryptography.DoubleRatchetSession>(); // Note: DoubleRatchetSession is stateful and usually created per-session, not as a singleton.
                                                                                // This registration might need to be adjusted if it's used directly by DI.
                                                                                // DirectSessionManager will create instances as needed.

            // Existing message repositories/stores
            services.AddSingleton<IMessageRepository, InMemoryMessageRepository>();
            services.AddSingleton<IMessageStore, InMemoryMessageStore>();

            return services;
        }
    }
}
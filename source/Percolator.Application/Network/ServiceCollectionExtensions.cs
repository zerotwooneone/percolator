using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Percolator.Application.Configuration;
using Percolator.Application.PeerDiscovery;
using Percolator.Application.ReverseSignal;
using Percolator.Network;
using Microsoft.Extensions.Logging;
using Percolator.Application.Network.Messaging;

namespace Percolator.Application.Network;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddNetworkServices(this IServiceCollection services, IConfiguration configuration)
    {
        // Bind the configuration section to the config object
        var config = new PeerDiscoveryConfig();
        configuration.GetSection("PeerDiscovery").Bind(config);
        services.AddSingleton<IPeerDiscoveryConfig>(config);

        services.Configure<NodeOptions>(configuration.GetSection(NodeOptions.SectionName));

        // Register the adapter that bridges the Network and Cryptography domains
        services.AddSingleton<Percolator.Network.ISigningService, SigningService>();

        // Cryptography signing service is used by identity-scoped ingress paths (e.g., StandardHandshakeIngress)
        services.AddSingleton<Percolator.Cryptography.ISigningService, Percolator.Cryptography.EcdsaSigningService>();

        // Register the core service from the Network domain library
        services.AddSingleton<IPeerDiscoveryService, PeerDiscoveryService>();

        // Domain-level route planner for PeerRoutingProfile selection
        services.AddSingleton<IProfileRoutePlanner, SimpleRoutePlanner>();

        services.Configure<ReverseSignalOptions>(configuration.GetSection(ReverseSignalOptions.SectionName));
        services.AddScoped<ICallbackEndpointValidator, CallbackEndpointValidator>();

        services.Configure<SimulatorWireTapOptions>(configuration.GetSection(SimulatorWireTapOptions.SectionName));
        services.AddSingleton<IOutboundMessageWireTap, OutboundMessageWireTap>();

        services.AddScoped<IInviteHandshakeResponseDeliveryService, InviteHandshakeResponseDeliveryService>();

        services.AddScoped<IInviteHandshakeResponseIngress, InviteHandshakeResponseIngress>();

        services.AddScoped<IStandardHandshakeIngress, StandardHandshakeIngress>();

        services.AddSingleton<IAdvertisedHostLookup, ConfigurationAdvertisedHostLookup>();

        services.AddScoped<IMainReverseSignalInviteFactory, MainReverseSignalInviteFactory>();

        // Register the hosted service that runs the discovery (can be disabled in tests)
        var discoveryEnabled = configuration.GetValue<bool>("PeerDiscovery:Enabled", true);
        if (discoveryEnabled)
        {
            services.AddHostedService<PeerDiscoveryHostedService>();
        }

        services.AddScoped<PercolatorMessageService>();
        services.AddSingleton<IPeerDiscoveryHandler, PeerDiscoveryHandler>();

        // Relay orchestrator for queued messages ACK flow
        services.AddScoped<RelayOrchestrator>();

        // Network messaging strategy services (legacy components removed)

        // Application-layer envelope sender
        services.AddScoped<IMessageService, MessageService>();
        services.AddScoped<Handshake.IEstablishSessionResponseValidator, Handshake.EstablishSessionResponseValidator>();
        services.AddScoped<Handshake.IInitiatorFinalizeService, Handshake.InitiatorFinalizeService>();
        services.AddTransient<IRemoteEnvelopeSender, RemoteEnvelopeSender>();

        // Establish direct session flow: service that creates peer/route and enqueues crypto pending session
        services.AddScoped<IEstablishDirectSessionService, EstablishDirectSessionService>();

        // Register domain Network.Messaging components required by MessageService
        services.AddScoped<Percolator.Network.Messaging.IRelayTopology, Percolator.Network.Messaging.DefaultRelayTopology>();
        services.AddScoped<IRouteSender, RouteSender>();
        services.AddScoped<Percolator.Network.Messaging.ISendExecutor, Percolator.Application.Network.Messaging.DefaultSendExecutor>();
        services.AddScoped<Percolator.Network.Messaging.INetworkSender, Percolator.Application.Network.Messaging.DefaultNetworkSender>();

        // Route confirmation service for promoting candidates to confirmed profiles
        services.AddScoped<IRouteConfirmationService, RouteConfirmationService>();

        return services;
    }
}

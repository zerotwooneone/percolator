using MediatR;
using Microsoft.Extensions.Logging;
using Percolator.Application.Messaging;
using Percolator.Application.Network;
using Percolator.Identity;
using Percolator.Identity.DomainEvents;

namespace Percolator.Infrastructure.Network;

public class ActiveIdentityLoadedEventHandler 
    : INotificationHandler<DomainEventNotification<ActiveIdentityLoadedEvent>>
{
    private readonly IGrpcServerManager _grpcServerManager;
    private readonly IIdentityNetworkService _networkService;
    private readonly ISelfIdentityRepository _identityRepository;
    private readonly IPublisher _publisher;
    private readonly ILogger<ActiveIdentityLoadedEventHandler> _logger;

    public ActiveIdentityLoadedEventHandler(
        IGrpcServerManager grpcServerManager,
        IIdentityNetworkService networkService,
        ISelfIdentityRepository identityRepository,
        IPublisher publisher,
        ILogger<ActiveIdentityLoadedEventHandler> logger)
    {
        _grpcServerManager = grpcServerManager;
        _networkService = networkService;
        _identityRepository = identityRepository;
        _publisher = publisher;
        _logger = logger;
    }

    public async Task Handle(DomainEventNotification<ActiveIdentityLoadedEvent> notification, CancellationToken ct)
    {
        var identityId = notification.DomainEvent.IdentityId;

        // Resolve the full identity from repository
        var identity = await _identityRepository.GetByIdAsync(identityId, ct);
        if (identity is null)
        {
            _logger.LogError("Identity {IdentityId} not found", identityId.Value);
            return;
        }

        // Stop existing server if running
        await _grpcServerManager.StopAsync(ct);

        var result = await _grpcServerManager.StartAsync(identity.Id, identity.ListeningPort, ct);

        if (!result.Success && result.IsPortConflict)
        {
            await _networkService.ResolvePortContentionAsync(identityId, ct);

            // Reload identity with updated port
            var updatedIdentity = await _identityRepository.GetByIdAsync(identityId, ct);
            if (updatedIdentity is null)
            {
                _logger.LogError("Identity {IdentityId} not found after port reassignment", identityId.Value);
                await _publisher.Publish(new NodeOfflineNotification(identityId, "Identity not found after port reassignment"), ct);
                return;
            }

            result = await _grpcServerManager.StartAsync(updatedIdentity.Id, updatedIdentity.ListeningPort, ct);

            if (!result.Success)
            {
                var errorMessage = result.ErrorMessage ?? "Unknown error";
                _logger.LogError(errorMessage, "Failed to start gRPC server after port reassignment");

                // Escalate fatal infrastructure failure to Application/Presentation layer
                await _publisher.Publish(new NodeOfflineNotification(identityId, errorMessage), ct);
            }
        }
    }
}

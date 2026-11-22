namespace Percolator.Application.Connections;

using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Percolator.Application.Handshake;
using Percolator.Application.Ports;
using Percolator.Application.Profiles;
using Percolator.Domain.Channels.Model;
using Percolator.Domain.Channels.Ports;
using Percolator.Domain.Channels.ValueObjects;
using Percolator.Domain.Common;
using Percolator.Domain.Identities;
using Percolator.Domain.Identities.Model;
using Percolator.Domain.Identities.ValueObjects;
using Percolator.PluginSdk;
using Percolator.PluginSdk.Events;
using Percolator.PluginSdk.Profiles;

/// <summary>
/// Scoped application workflow coordinating acceptance, rejection, and blocking of quarantined inbound contact requests.
/// Enforces domain boundary rules and completes peer handshake ratchets ([SYS-BLOCK-WIRE-INDISTINGUISHABILITY], [DEC-PULL-BASED-CONSENT-STAGING]).
/// </summary>
public sealed class InboundConnectionWorkflow : IInboundConnectionWorkflow
{
    private readonly IContactRequestCoordinator _contactCoordinator;
    private readonly IHandshakeService _handshakeService;
    private readonly IPendingHandshakeRepository _pendingHandshakeRepo;
    private readonly IChannelRepository _channelRepo;
    private readonly IPeerMailboxRoutingRepository _routingRepo;
    private readonly IDateTimeProvider _timeProvider;
    private readonly IEventPublisher _eventPublisher;
    private readonly ILogger<InboundConnectionWorkflow> _logger;

    public InboundConnectionWorkflow(
        IContactRequestCoordinator contactCoordinator,
        IHandshakeService handshakeService,
        IPendingHandshakeRepository pendingHandshakeRepo,
        IChannelRepository channelRepo,
        IPeerMailboxRoutingRepository routingRepo,
        IDateTimeProvider timeProvider,
        IEventPublisher eventPublisher,
        ILogger<InboundConnectionWorkflow> logger)
    {
        _contactCoordinator = contactCoordinator ?? throw new ArgumentNullException(nameof(contactCoordinator));
        _handshakeService = handshakeService ?? throw new ArgumentNullException(nameof(handshakeService));
        _pendingHandshakeRepo = pendingHandshakeRepo ?? throw new ArgumentNullException(nameof(pendingHandshakeRepo));
        _channelRepo = channelRepo ?? throw new ArgumentNullException(nameof(channelRepo));
        _routingRepo = routingRepo ?? throw new ArgumentNullException(nameof(routingRepo));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _eventPublisher = eventPublisher ?? throw new ArgumentNullException(nameof(eventPublisher));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<DomainResult<bool>> AcceptRequestAsync(
        PublicIdentityId personaId,
        PublicIdentityId requesterId,
        CancellationToken ct = default)
    {
        if (!personaId.IsValid || !requesterId.IsValid)
        {
            return DomainResult<bool>.Failure(new DomainError("INVALID_IDENTITY", "Invalid persona or requester identity ID."));
        }

        _logger.LogInformation("Accepting inbound contact request from {RequesterId} for persona {PersonaId}", requesterId, personaId);

        var approveResult = await _contactCoordinator.ApproveRequestAsync(
            personaId, requesterId, PeerTrustLevel.Tofu, ct).ConfigureAwait(false);

        if (!approveResult.IsSuccess)
        {
            _logger.LogWarning("Failed to approve contact {RequesterId}: {Error}", requesterId, approveResult.Error.Description);
            return DomainResult<bool>.Failure(approveResult.Error);
        }

        var completeResult = await _handshakeService.CompletePendingHandshakeAsync(
            personaId, requesterId, ct).ConfigureAwait(false);

        if (!completeResult.IsSuccess)
        {
            _logger.LogWarning("Failed to complete pending handshake for {RequesterId}: {Error}. Reverting contact approval.", requesterId, completeResult.Error.Description);
            await _contactCoordinator.RejectRequestAsync(personaId, requesterId, block: false, ct).ConfigureAwait(false);
            return DomainResult<bool>.Failure(completeResult.Error);
        }

        var channelId = ChannelId.DeterministicForDirect(personaId, requesterId);
        var routing = await _routingRepo.GetByPeerIdAsync(requesterId, ct).ConfigureAwait(false);
        var relayEndpoint = routing?.RelayEndpoint;

        var existingChannel = await _channelRepo.GetDirectByIdAsync(channelId, ct).ConfigureAwait(false);
        DirectChannel channel;
        if (existingChannel == null)
        {
            var channelResult = DirectChannel.Create(
                channelId,
                personaId,
                requesterId,
                _timeProvider,
                DirectChannelStatus.Active,
                relayEndpoint);

            if (!channelResult.IsSuccess)
            {
                _logger.LogWarning("Failed to create direct channel for {RequesterId}: {Error}", requesterId, channelResult.Error.Description);
                return DomainResult<bool>.Failure(channelResult.Error);
            }

            channel = channelResult.Value!;
        }
        else
        {
            channel = existingChannel;
            channel.Activate(_timeProvider, relayEndpoint);
        }

        await _channelRepo.SaveDirectAsync(channel, ct).ConfigureAwait(false);

        try
        {
            foreach (var domainEvt in channel.DomainEvents)
            {
                var appEvt = domainEvt.ToApplicationEvent();
                if (appEvt != null)
                {
                    await _eventPublisher.PublishAsync(appEvt, ct).ConfigureAwait(false);
                }
                else
                {
                    _logger.LogWarning("Unmapped domain event {EventType} skipped during channel event dispatch", domainEvt.GetType().Name);
                }
            }
        }
        finally
        {
            channel.ClearDomainEvents();
        }

        return DomainResult<bool>.Success(true);
    }

    public async Task<DomainResult<bool>> RejectRequestAsync(
        PublicIdentityId personaId,
        PublicIdentityId requesterId,
        CancellationToken ct = default)
    {
        if (!personaId.IsValid || !requesterId.IsValid)
        {
            return DomainResult<bool>.Failure(new DomainError("INVALID_IDENTITY", "Invalid persona or requester identity ID."));
        }

        _logger.LogInformation("Rejecting inbound request from {RequesterId} for persona {PersonaId}", requesterId, personaId);

        try
        {
            await _pendingHandshakeRepo.DeletePendingHandshakeAsync(personaId, requesterId, ct).ConfigureAwait(false);
            var rejectResult = await _contactCoordinator.RejectRequestAsync(personaId, requesterId, block: false, ct).ConfigureAwait(false);
            if (!rejectResult.IsSuccess)
            {
                return DomainResult<bool>.Failure(rejectResult.Error);
            }

            return DomainResult<bool>.Success(true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Exception while rejecting request from {RequesterId}", requesterId);
            return DomainResult<bool>.Failure(new DomainError("REJECT_FAILED", ex.Message));
        }
    }

    public async Task<DomainResult<bool>> BlockRequestAsync(
        PublicIdentityId personaId,
        PublicIdentityId requesterId,
        CancellationToken ct = default)
    {
        if (!personaId.IsValid || !requesterId.IsValid)
        {
            return DomainResult<bool>.Failure(new DomainError("INVALID_IDENTITY", "Invalid persona or requester identity ID."));
        }

        _logger.LogInformation("Blocking requester {RequesterId} per SYS-BLOCK-WIRE-INDISTINGUISHABILITY", requesterId);

        try
        {
            await _pendingHandshakeRepo.DeletePendingHandshakeAsync(personaId, requesterId, ct).ConfigureAwait(false);
            var rejectResult = await _contactCoordinator.RejectRequestAsync(personaId, requesterId, block: true, ct).ConfigureAwait(false);
            if (!rejectResult.IsSuccess)
            {
                return DomainResult<bool>.Failure(rejectResult.Error);
            }

            return DomainResult<bool>.Success(true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Exception while blocking requester {RequesterId}", requesterId);
            return DomainResult<bool>.Failure(new DomainError("BLOCK_FAILED", ex.Message));
        }
    }
}

using Microsoft.Extensions.Logging;
using Percolator.Contracts;
using Percolator.Chat.Messaging.ValueObjects;
using Percolator.Network;
using PeerId = Percolator.Identity.PeerId;

namespace Percolator.Application.Network
{
    public sealed class RemoteEnvelopeSender : IRemoteEnvelopeSender
    {
        private readonly ILogger<RemoteEnvelopeSender> _logger;
        private readonly IMessageService _messageService;
        private readonly IPeerRoutingProfileRepository _routingProfileRepository;

        public RemoteEnvelopeSender(
            ILogger<RemoteEnvelopeSender> logger,
            IMessageService messageService,
            IPeerRoutingProfileRepository routingProfileRepository)
        {
            _logger = logger;
            _messageService = messageService;
            _routingProfileRepository = routingProfileRepository;
        }

        public async Task SendChatEnvelopeToPeerAsync(ChatEnvelope chatEnvelope, RecipientRoute recipient, CancellationToken ct = default)
        {
            var internalEnvelope = new InternalEnvelope { ChatEnvelope = chatEnvelope };
            try
            {
                var result = await _messageService
                    .SendMessageAsync(internalEnvelope, recipient.PeerId, ct)
                    .ConfigureAwait(false);
                if (result.LastError is not null)
                {
                    _logger.LogWarning(result.LastError, "Failed sending envelope to {PeerId} via {Path}", recipient.PeerId, result.Path);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed sending envelope to {PeerId}", recipient.PeerId);
            }
        }

        public async Task SendChatEnvelopeToPeerAsync(ChatEnvelope chatEnvelope, Pkh destinationPkh, CancellationToken ct = default)
        {
            // Convert Chat domain Pkh to Network domain PublicKeyHash (zero-allocation span conversion)
            var publicKeyHash = Percolator.Network.PublicKeyHash.FromSpan(destinationPkh.Span);

            // Look up routing profile by public key hash to resolve the endpoint
            var routingProfile = await _routingProfileRepository.GetByPublicKeyHashAsync(publicKeyHash, ct).ConfigureAwait(false);

            if (routingProfile is null)
            {
                _logger.LogWarning("No routing profile found for PKH, cannot send envelope");
                return;
            }

            if (routingProfile.Id is null)
            {
                _logger.LogWarning("No peer id found for routing profile, cannot send envelope");
                return;
            }

            // Use the PeerId from the routing profile to send via existing method
            var recipient = new RecipientRoute(new PeerId(routingProfile.Id.Value.Value), null);
            await SendChatEnvelopeToPeerAsync(chatEnvelope, recipient, ct).ConfigureAwait(false);
        }
    }
}

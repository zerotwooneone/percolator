using Microsoft.Extensions.Logging;
using Percolator.Contracts;
using PeerId = Percolator.Identity.PeerId;

namespace Percolator.Application.Network
{
    public sealed class RemoteEnvelopeSender : IRemoteEnvelopeSender
    {
        private readonly ILogger<RemoteEnvelopeSender> _logger;
        private readonly IMessageService _messageService;

        public RemoteEnvelopeSender(
            ILogger<RemoteEnvelopeSender> logger,
            IMessageService messageService)
        {
            _logger = logger;
            _messageService = messageService;
        }

        public async Task SendChatEnvelopeToPeerAsync(ChatEnvelope chatEnvelope, PeerId recipientPeerId, CancellationToken ct = default)
        {
            var internalEnvelope = new InternalEnvelope { ChatEnvelope = chatEnvelope };
            try
            {
                var result = await _messageService
                    .SendMessageAsync(internalEnvelope, recipientPeerId, ct)
                    .ConfigureAwait(false);
                if (result.LastError is not null)
                {
                    _logger.LogWarning(result.LastError, "Failed sending envelope to {PeerId} via {Path}", recipientPeerId, result.Path);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed sending envelope to {PeerId}", recipientPeerId);
            }
        }
    }
}

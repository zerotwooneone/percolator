using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Percolator.Application.Apps.Chat
{
    // Temporary stub: returns null so import becomes a no-op until transport is wired.
    internal sealed class DefaultTransportKeyResolver : ITransportKeyResolver
    {
        private readonly ILogger<DefaultTransportKeyResolver> _logger;
        public DefaultTransportKeyResolver(ILogger<DefaultTransportKeyResolver> logger)
        {
            _logger = logger;
        }
        public Task<byte[]?> GetAeadKeyAsync(Guid conversationId, CancellationToken ct)
        {
            _logger.LogDebug("[DefaultTransportKeyResolver] No AEAD key for conversation {ConversationId}", conversationId);
            return Task.FromResult<byte[]?>(null);
        }

        public Task<byte[]?> GetAeadKeyAsync(Guid conversationId, Guid remotePeerId, CancellationToken ct)
        {
            _logger.LogDebug("[DefaultTransportKeyResolver] No AEAD key for conversation {ConversationId} and peer {PeerId}", conversationId, remotePeerId);
            return Task.FromResult<byte[]?>(null);
        }
    }
}

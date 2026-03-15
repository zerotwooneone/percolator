using Percolator.Application.Apps.Chat;
using Microsoft.Extensions.Logging;

namespace Percolator.Infrastructure.Chat
{
    // Placeholder resolver: returns null so sender skips until we can resolve acting admin from metadata/state.
    internal sealed class NullActingAdminResolver : IActingAdminResolver
    {
        private readonly ILogger<NullActingAdminResolver> _logger;
        public NullActingAdminResolver(ILogger<NullActingAdminResolver> logger)
        {
            _logger = logger;
        }

        public Task<Guid?> GetActingAdminPeerIdAsync(Guid conversationId, CancellationToken ct)
        {
            _logger.LogDebug("[NullActingAdminResolver] No acting admin available for conversation {ConversationId}", conversationId);
            return Task.FromResult<Guid?>(null);
        }
    }
}

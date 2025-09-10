using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Percolator.Chat.App;
using Percolator.Chat.Primitives;
using Percolator.Chat.ValueObjects;
using Percolator.Cryptography;

namespace Percolator.Application.Apps.Chat
{
    // Application-layer adapter that bridges Chat -> Cryptography.GroupManager.
    // For now, this is a placeholder that will later resolve the right GroupManager instance/state per conversation.
    internal sealed class GroupKeyOperations : IGroupKeyOperations
    {
        private readonly ILogger<GroupKeyOperations> _logger;
        public GroupKeyOperations(ILogger<GroupKeyOperations> logger)
        {
            _logger = logger;
        }

        public Task ImportGroupKeyAsync(Guid conversationId, GroupKeyVersion version, EncryptedGroupKey encryptedKey, CancellationToken ct)
        {
            // TODO: Resolve GroupManager for this conversation and import the new key material.
            // This requires a mapping from ConversationId to GroupManager state and proper key decryption.
            _logger.LogInformation("[GroupKeyOperations] ImportGroupKeyAsync convo={ConversationId} version={Version} bytes={Length}", conversationId, version.Value, encryptedKey.Value.Length);
            return Task.CompletedTask;
        }
    }
}

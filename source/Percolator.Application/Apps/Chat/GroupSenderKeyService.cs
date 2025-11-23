using System;
using System.Threading;
using System.Threading.Tasks;
using Percolator.Chat.Primitives;
using Percolator.Chat.ValueObjects;

namespace Percolator.Application.Apps.Chat
{
    internal class GroupSenderKeyService : IGroupSenderKeyService
    {
        private readonly IGroupSenderKeyRepository _repository;
        private readonly IEnvelopeCrypto _crypto;

        public GroupSenderKeyService(IGroupSenderKeyRepository repository, IEnvelopeCrypto crypto)
        {
            _repository = repository;
            _crypto = crypto;
        }

        public async Task ImportSenderKeyAsync(Guid conversationId, GroupKeyVersion keyVersion, EncryptedGroupKey encryptedKey, CancellationToken cancellationToken)
        {
            if (conversationId == Guid.Empty) throw new ArgumentException("conversationId must not be empty", nameof(conversationId));
            // idempotent check
            if (await _repository.ExistsAsync(conversationId, keyVersion, cancellationToken).ConfigureAwait(false))
            {
                return;
            }

            var (chainKey, signingKey) = await _crypto.DecryptAsync(conversationId, encryptedKey, cancellationToken).ConfigureAwait(false);
            await _repository.UpsertAsync(conversationId, keyVersion, chainKey, signingKey, cancellationToken).ConfigureAwait(false);
        }
    }
}

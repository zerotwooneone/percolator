using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using System.Security.Cryptography;
using Percolator.Chat.App;
using Percolator.Chat.Primitives;
using Percolator.Chat.ValueObjects;
using Percolator.Cryptography;
using MediatR;

namespace Percolator.Application.Apps.Chat
{
    // Application-layer adapter that bridges Chat -> Cryptography.GroupManager.
    // For now, this is a placeholder that will later resolve the right GroupManager instance/state per conversation.
    internal sealed class GroupKeyOperations : IGroupKeyOperations
    {
        private readonly ILogger<GroupKeyOperations> _logger;
        private readonly IGroupManagerResolver _resolver;
        private readonly ITransportKeyResolver _transportKeyResolver;
        private readonly IGroupManagerStateStore _gmStateStore;
        private readonly IAtRestKeyProvider _atRestKeyProvider;
        private readonly IMediator _mediator;

        public GroupKeyOperations(ILogger<GroupKeyOperations> logger, IGroupManagerResolver resolver, ITransportKeyResolver transportKeyResolver, IGroupManagerStateStore gmStateStore, IAtRestKeyProvider atRestKeyProvider, IMediator mediator)
        {
            _logger = logger;
            _resolver = resolver;
            _transportKeyResolver = transportKeyResolver;
            _gmStateStore = gmStateStore;
            _atRestKeyProvider = atRestKeyProvider;
            _mediator = mediator;
        }

        public async Task ImportGroupKeyAsync(Guid conversationId, GroupKeyVersion version, EncryptedGroupKey encryptedKey, CancellationToken ct)
        {
            if (!_resolver.TryGet(conversationId, out GroupManager manager))
            {
                _logger.LogWarning("[GroupKeyOperations] No GroupManager available for conversation {ConversationId}; cannot import key v{Version}", conversationId, version.Value);
                return;
            }

            // Parse envelope (format defined in KeyEnvelope)
            KeyEnvelope env;
            try
            {
                env = KeyEnvelope.Parse(encryptedKey.Value);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[GroupKeyOperations] Failed to parse key envelope for conversation {ConversationId}", conversationId);
                return;
            }

            // Version/algorithm dispatch (placeholder)
            switch (env.Version)
            {
                case 1:
                    // Example: AlgorithmId 1 = AES-GCM 256
                    if (env.AlgorithmId != 1)
                    {
                        _logger.LogError("[GroupKeyOperations] Unsupported algorithm id {Alg} for envelope v1 (conversation {ConversationId})", env.AlgorithmId, conversationId);
                        return;
                    }
                    break;
                default:
                    _logger.LogError("[GroupKeyOperations] Unsupported envelope version {Version} (conversation {ConversationId})", env.Version, conversationId);
                    return;
            }

            // Resolve transport AEAD key (per-recipient) from Double Ratchet/session
            var aeadKey = await _transportKeyResolver.GetAeadKeyAsync(conversationId, ct);
            if (aeadKey is null || (aeadKey.Length != 16 && aeadKey.Length != 32))
            {
                _logger.LogWarning("[GroupKeyOperations] Missing/invalid AEAD key for conversation {ConversationId}; cannot decrypt key v{Version}", conversationId, version.Value);
                return;
            }

            try
            {
                var plaintext = new byte[env.Ciphertext.Length];
                using var aead = new AesGcm(aeadKey, env.Tag.Length);
                aead.Decrypt(env.Nonce, env.Ciphertext, env.Tag, plaintext, associatedData: null);

                // Import/activate the new group key in GroupManager via DDD API
                var material = new GroupKeyMaterial(plaintext);
                var verC = new GroupKeyVersionC(version.Value);
                manager.ImportKey(verC, material);
                _logger.LogInformation("[GroupKeyOperations] Imported group key v{Version} for conversation {ConversationId} (len={Len})", version.Value, conversationId, plaintext.Length);

                // Persist updated GroupManager state
                var masterKey = await _atRestKeyProvider.GetMasterKeyAsync(ct);
                var stateBlob = manager.SaveState(masterKey);
                await _gmStateStore.SaveAsync(conversationId, stateBlob, DateTimeOffset.UtcNow, ct);

                // Notify application that this node adopted key version successfully
                await _mediator.Publish(new KeyVersionAdoptedNotification(conversationId, (uint)version.Value), ct);
            }
            catch (CryptographicException ex)
            {
                _logger.LogError(ex, "[GroupKeyOperations] AEAD decrypt failed for conversation {ConversationId}", conversationId);
                return;
            }
            return;
        }
    }
}

using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Percolator.Application.Identity;
using Percolator.Cryptography;

namespace Percolator.Application.Apps.Chat
{
    // Derives an AEAD key for group key envelopes from the current Double Ratchet session's root key
    internal sealed class TransportKeyResolver : ITransportKeyResolver
    {
        private readonly ILogger<TransportKeyResolver> _logger;
        private readonly ActiveIdentityContext _activeIdentityContext;
        private readonly IDirectSessionConversationLookup _conversationLookup;
        private readonly IDoubleRatchetSessionStore _sessionStore;

        public TransportKeyResolver(
            ILogger<TransportKeyResolver> logger,
            ActiveIdentityContext activeIdentityContext,
            IDirectSessionConversationLookup conversationLookup,
            IDoubleRatchetSessionStore sessionStore)
        {
            _logger = logger;
            _activeIdentityContext = activeIdentityContext;
            _conversationLookup = conversationLookup;
            _sessionStore = sessionStore;
        }

        public async Task<byte[]?> GetAeadKeyAsync(Guid conversationId, CancellationToken ct)
        {
            if (_activeIdentityContext.Identity is null)
            {
                _logger.LogError("[TransportKeyResolver] Active identity not loaded; cannot resolve AEAD key.");
                return null;
            }

            var selfIdentityId = _activeIdentityContext.Identity.SelfIdentityId;
            var directSessionId = await _conversationLookup.GetDirectSessionIdAsync(conversationId, selfIdentityId, ct).ConfigureAwait(false);
            if (directSessionId is null)
            {
                _logger.LogWarning("[TransportKeyResolver] No direct session found for conversation {ConversationId}", conversationId);
                return null;
            }

            var state = await _sessionStore.GetSessionStateAsync(new SessionId(directSessionId.Value), selfIdentityId).ConfigureAwait(false);
            if (state is null || state.RootKey is null)
            {
                _logger.LogWarning("[TransportKeyResolver] No session state/root key for direct session {SessionId}", directSessionId);
                return null;
            }

            // Derive a dedicated AEAD key for envelopes from the ratchet root key
            var aeadKey = CryptoUtils.KDF(salt: null, key: state.RootKey.Value, info: "group-envelope-aead", outputLength: CryptoUtils.KeySize);
            return aeadKey;
        }

        public async Task<byte[]?> GetAeadKeyAsync(Guid conversationId, Guid remotePeerId, CancellationToken ct)
        {
            if (_activeIdentityContext.Identity is null)
            {
                _logger.LogError("[TransportKeyResolver] Active identity not loaded; cannot resolve AEAD key.");
                return null;
            }

            var selfIdentityId = _activeIdentityContext.Identity.SelfIdentityId;
            var directSessionId = await _conversationLookup.GetDirectSessionIdAsync(conversationId, selfIdentityId, remotePeerId, ct).ConfigureAwait(false)
                                  ?? await _conversationLookup.GetDirectSessionIdAsync(conversationId, selfIdentityId, ct).ConfigureAwait(false);
            if (directSessionId is null)
            {
                _logger.LogWarning("[TransportKeyResolver] No direct session found for conversation {ConversationId} and peer {PeerId}", conversationId, remotePeerId);
                return null;
            }

            var state = await _sessionStore.GetSessionStateAsync(new SessionId(directSessionId.Value), selfIdentityId).ConfigureAwait(false);
            if (state is null || state.RootKey is null)
            {
                _logger.LogWarning("[TransportKeyResolver] No session state/root key for direct session {SessionId}", directSessionId);
                return null;
            }

            var aeadKey = CryptoUtils.KDF(salt: null, key: state.RootKey.Value, info: "group-envelope-aead", outputLength: CryptoUtils.KeySize);
            return aeadKey;
        }
    }
}

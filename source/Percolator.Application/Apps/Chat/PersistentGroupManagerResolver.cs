using System;
using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Percolator.Cryptography;
using Percolator.Application.Identity;

namespace Percolator.Application.Apps.Chat
{
    // Loads/saves GroupManager per conversation using IGroupManagerStateStore and an at-rest master key
    internal sealed class PersistentGroupManagerResolver : IGroupManagerResolver, IDisposable
    {
        private readonly ILogger<PersistentGroupManagerResolver> _logger;
        private readonly ILoggerFactory _loggerFactory;
        private readonly IOptions<CryptographyOptions> _cryptoOptions;
        private readonly IGroupManagerStateStore _stateStore;
        private readonly IAtRestKeyProvider _atRestKeyProvider;
        private readonly ActiveIdentityContext _activeIdentityContext;
        private readonly ConcurrentDictionary<Guid, GroupManager> _cache = new();

        public PersistentGroupManagerResolver(
            ILogger<PersistentGroupManagerResolver> logger,
            ILoggerFactory loggerFactory,
            IOptions<CryptographyOptions> cryptoOptions,
            IGroupManagerStateStore stateStore,
            IAtRestKeyProvider atRestKeyProvider,
            ActiveIdentityContext activeIdentityContext)
        {
            _logger = logger;
            _loggerFactory = loggerFactory;
            _cryptoOptions = cryptoOptions;
            _stateStore = stateStore;
            _atRestKeyProvider = atRestKeyProvider;
            _activeIdentityContext = activeIdentityContext;
        }

        public bool TryGet(Guid conversationId, out GroupManager manager)
        {
            if (_cache.TryGetValue(conversationId, out manager!))
            {
                return true;
            }

            // Lazy-load from state or create new
            manager = LoadOrCreate(conversationId);
            if (manager is not null)
            {
                _cache[conversationId] = manager;
                return true;
            }
            return false;
        }

        private GroupManager LoadOrCreate(Guid conversationId)
        {
            if (_activeIdentityContext.Keys is null)
            {
                _logger.LogError("[PersistentGroupManagerResolver] Active identity keys not loaded; cannot create/load GroupManager.");
                return null!;
            }
            var identityKey = _activeIdentityContext.Keys.IdentitySigningKey;
            var stateBlob = _stateStore.GetAsync(conversationId, default).GetAwaiter().GetResult();
            if (stateBlob is { Length: > 0 })
            {
                var masterKey = _atRestKeyProvider.GetMasterKeyAsync(default).GetAwaiter().GetResult();
                var gm = GroupManager.LoadState(stateBlob, masterKey, identityKey, _loggerFactory, _cryptoOptions);
                _logger.LogInformation("[PersistentGroupManagerResolver] Loaded GroupManager for conversation {ConversationId}", conversationId);
                return gm;
            }
            else
            {
                var gm = new GroupManager(identityKey, _loggerFactory, _cryptoOptions);
                _logger.LogInformation("[PersistentGroupManagerResolver] Created new GroupManager for conversation {ConversationId}", conversationId);
                return gm;
            }
        }

        public void Dispose()
        {
            foreach (var kv in _cache)
            {
                kv.Value.Dispose();
            }
            _cache.Clear();
        }
    }
}

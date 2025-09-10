using System;
using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Percolator.Cryptography;

namespace Percolator.Application.Apps.Chat
{
    // Minimal, in-memory resolver. In a real app, this would track GroupManager lifecycle per conversation.
    internal sealed class DefaultGroupManagerResolver : IGroupManagerResolver
    {
        private readonly ILogger<DefaultGroupManagerResolver> _logger;
        private readonly ConcurrentDictionary<Guid, GroupManager> _managers = new();

        public DefaultGroupManagerResolver(ILogger<DefaultGroupManagerResolver> logger)
        {
            _logger = logger;
        }

        public bool TryGet(Guid conversationId, out GroupManager manager)
        {
            return _managers.TryGetValue(conversationId, out manager!);
        }

        // Temporary helpers to allow registration when wiring up GroupManager lifecycle later.
        public void Register(Guid conversationId, GroupManager manager)
        {
            _managers[conversationId] = manager;
        }

        public void Remove(Guid conversationId)
        {
            _managers.TryRemove(conversationId, out _);
        }
    }
}

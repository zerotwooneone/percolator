using System;
using Percolator.Cryptography;

namespace Percolator.Application.Apps.Chat
{
    // Application-layer resolver to get a GroupManager instance for a conversation, if any.
    public interface IGroupManagerResolver
    {
        bool TryGet(Guid conversationId, out GroupManager manager);
    }
}

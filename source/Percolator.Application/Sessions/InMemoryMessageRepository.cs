using System.Collections.Concurrent;
using Percolator.Sessions;

namespace Percolator.Application.Sessions;

public class InMemoryMessageRepository : IMessageRepository
{
    private readonly ConcurrentDictionary<ConversationId, List<DirectMessage>> _messages =
        new();

    public Task AddAsync(DirectMessage message)
    {
        var messageList = _messages.GetOrAdd(message.ConversationId, _ => new List<DirectMessage>());
        lock (messageList)
        {
            messageList.Add(message);
        }

        return Task.CompletedTask;
    }
}

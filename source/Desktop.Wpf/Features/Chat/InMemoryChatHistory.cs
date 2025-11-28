using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Desktop.Wpf.Features.Chat;

public sealed class InMemoryChatHistory : IChatHistory
{
    private readonly ConcurrentDictionary<string, List<ChatMessage>> _store = new();

    public InMemoryChatHistory()
    {
        _store.TryAdd("1", new()
        {
            new ChatMessage { Id = "m1", Author = "Sarah Connor", Text = "The encryption keys have been rotated.", TimestampText = "10:41 AM", IsOwn = false },
            new ChatMessage { Id = "m2", Author = "Me", Text = "Copy. Updating agents now.", TimestampText = "10:42 AM", IsOwn = true }
        });
        _store.TryAdd("2", new()
        {
            new ChatMessage { Id = "m1", Author = "Morpheus", Text = "Follow the white rabbit.", TimestampText = "Yesterday", IsOwn = false }
        });

        // Mark last outgoing as delivered/read for demo
        if (_store.TryGetValue("1", out var list) && list.LastOrDefault(m => m.IsOwn) is ChatMessage lastOwn)
        {
            lastOwn.IsDelivered.Value = true;
            lastOwn.IsRead.Value = true;
        }
    }

    public Task<IReadOnlyList<ChatMessage>> GetMessagesAsync(string sessionId, CancellationToken ct)
    {
        var list = _store.GetOrAdd(sessionId, _ => new());
        return Task.FromResult<IReadOnlyList<ChatMessage>>(list.ToList());
    }

    public Task AppendAsync(string sessionId, ChatMessage message, CancellationToken ct)
    {
        var list = _store.GetOrAdd(sessionId, _ => new());
        list.Add(message);
        // Simulate async delivery/read transitions for own messages
        if (message.IsOwn)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(500, CancellationToken.None);
                    message.IsDelivered.Value = true;
                    await Task.Delay(1000, CancellationToken.None);
                    message.IsRead.Value = true;
                }
                catch { }
            });
        }
        return Task.CompletedTask;
    }
}

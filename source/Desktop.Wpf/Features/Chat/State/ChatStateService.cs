using ObservableCollections;
using Percolator.Chat.ValueObjects;
using Percolator.Network;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace Desktop.Wpf.Features.Chat.State;

public sealed class ChatStateService : IDisposable
{
    private readonly ConcurrentDictionary<DirectSessionId, ObservableList<ChatMessageModel>> _sessionMessages = new();
    private readonly object _stateGate = new();

    public ObservableList<ChatMessageModel> GetOrAddSessionMessagesList(DirectSessionId sessionId)
        => _sessionMessages.GetOrAdd(sessionId, _ => new ObservableList<ChatMessageModel>());

    public void SyncMessages(DirectSessionId sessionId, IReadOnlyList<ChatMessageSnapshot> snapshots)
    {
        var list = GetOrAddSessionMessagesList(sessionId);
        lock (_stateGate)
        {
            var existingById = list.ToDictionary(m => m.Id);
            foreach (var snap in snapshots)
            {
                if (existingById.TryGetValue(snap.Id, out var existing))
                {
                    existing.UpdateFromSnapshot(snap);
                }
                else
                {
                    list.Add(new ChatMessageModel(snap));
                }
            }
        }
    }

    public void OptimisticInsert(DirectSessionId sessionId, ChatMessageSnapshot snapshot)
    {
        lock (_stateGate)
        {
            var list = GetOrAddSessionMessagesList(sessionId);
            if (!list.Any(m => m.Id == snapshot.Id))
            {
                list.Add(new ChatMessageModel(snapshot));
            }
        }
    }

    public void MarkAsDelivered(DirectSessionId sessionId, MessageId messageId)
    {
        lock (_stateGate)
        {
            var list = GetOrAddSessionMessagesList(sessionId);
            var msg = list.FirstOrDefault(m => m.Id == messageId);
            if (msg is not null)
            {
                msg.IsDelivered.Value = true;
                msg.IsSending.Value = false;
            }
        }
    }

    public void Dispose()
    {
        lock (_stateGate)
        {
            foreach (var list in _sessionMessages.Values)
            {
                foreach (var msg in list) msg.Dispose();
                list.Clear();
            }
            _sessionMessages.Clear();
        }
    }
}

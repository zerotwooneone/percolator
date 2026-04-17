using ObservableCollections;
using R3;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace Desktop.Wpf.Features.Chat.State;

public sealed class ChatStateService : IDisposable
{
    private readonly ConcurrentDictionary<string, ObservableList<ChatMessageModel>> _sessionMessages = new();
    private readonly Subject<Unit> _stateMutated = new();
    private readonly object _stateGate = new();

    public Observable<Unit> StateMutated => _stateMutated;

    public ObservableList<ChatMessageModel> GetOrAddSessionMessagesList(string sessionId) 
        => _sessionMessages.GetOrAdd(sessionId, _ => new ObservableList<ChatMessageModel>());

    public void SyncMessages(string sessionId, IReadOnlyList<ChatMessageSnapshot> snapshots)
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
        
        _stateMutated.OnNext(Unit.Default);
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
        _stateMutated.Dispose();
    }
}

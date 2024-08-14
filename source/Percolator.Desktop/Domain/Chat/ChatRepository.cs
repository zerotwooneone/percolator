using System.Diagnostics.CodeAnalysis;
using Google.Protobuf;
using Microsoft.Extensions.Logging;
using R3;

namespace Percolator.Desktop.Domain.Chat;

public class ChatRepository
{
    private readonly ILogger<ChatRepository> _logger;
    private readonly List<ChatModel> _chats = new();
    public IReadOnlyCollection<ChatModel> Chats => _chats;
    public Observable<ByteString> Added => _added;
    private readonly Subject<ByteString> _added = new();
    public Observable<ByteString> Removed => _removed;
    private readonly Subject<ByteString> _removed = new();
 
    public ChatRepository(
        ILogger<ChatRepository> logger)
    {
        _logger = logger;
    }  
    public void Add(ChatModel chatModel)
    {
        var removed = _chats.RemoveAll(c => 
            c.RemoteClientModel.Identity.Equals(chatModel.RemoteClientModel.Identity) ||
            (c.SessionId.Value != null && c.SessionId.Value.Equals(chatModel.SessionId.Value)));

        if (removed != 0)
        {
            _logger.LogError("Removed {Removed} chat(s) for {Identity} with session:{SessionId}", 
                removed, chatModel.RemoteClientModel.Identity.ToBase64(), chatModel.SessionId.Value?.ToBase64());
            _removed.OnNext(chatModel.RemoteClientModel.Identity);
        }

        _chats.Add(chatModel);
        _added.OnNext(chatModel.RemoteClientModel.Identity);
    }
    public bool TryGetByIdentity(ByteString identity,[NotNullWhen(true)] out ChatModel? chat)
    {
        chat = _chats
            .FirstOrDefault(c => c.RemoteClientModel.Identity.Equals(identity));
        return chat != null;
    }
    public bool TryGetBySessionId(ByteString sessionId,[NotNullWhen(true)] out ChatModel? chat)
    {
        chat = _chats
            .FirstOrDefault(c => c.SessionId.Value?.Equals(sessionId) ?? false);
        return chat != null;
    }
}
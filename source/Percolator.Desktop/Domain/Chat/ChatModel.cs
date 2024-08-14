using System.Security.Cryptography;
using Google.Protobuf;
using Percolator.Desktop.Domain.Client;
using R3;

namespace Percolator.Desktop.Domain.Chat;

public class ChatModel
{
    public IList<MessageModel> Messages => _sortedMessages.Values;
    private readonly SortedList<DateTime,MessageModel> _sortedMessages = new();
    public RemoteClientModel RemoteClientModel { get; }
    
    public ReactiveProperty<RSACryptoServiceProvider?> Ephemeral { get; } = new();

    public ReadOnlyReactiveProperty<bool> CanReplyIntroduce { get; }
    public ReactiveProperty<Aes?> SessionKey { get; }= new();
    public ReactiveProperty<ByteString?> SessionId { get; } = new();
    public Observable<MessageModel> ChatMessage { get; }
    public ReactiveProperty<bool> IntroduceInProgress { get; }= new();

    private readonly Subject<MessageModel> _messageSubject = new();
    
    public ReadOnlyReactiveProperty<bool> CanIntroduce { get; }

    public ChatModel(RemoteClientModel remoteClientModel)
    {
        RemoteClientModel = remoteClientModel;
        CanReplyIntroduce = Ephemeral
            .CombineLatest(RemoteClientModel.SelectedIpAddress, (ephemeral, ip) => (ephemeral, ip))
            .Select(e => e.ephemeral != null && e.ip != null)
            .ToReadOnlyReactiveProperty();
        CanIntroduce = RemoteClientModel.SelectedIpAddress
            .Select(ip => ip != null)
            .ToReadOnlyReactiveProperty();
        ChatMessage = _messageSubject.AsObservable();
    }

    public void OnChatMessage(MessageModel messageModel)
    {
        _messageSubject.OnNext(messageModel);
    }
        
}
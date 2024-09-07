using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Unicode;
using Google.Protobuf;
using Microsoft.Extensions.Logging;
using Percolator.Crypto;
using Percolator.Crypto.Grpc;
using Percolator.Desktop.Domain.Client;
using Percolator.Desktop.Main;
using Percolator.Protobuf.Introduce;
using R3;
using DoubleRatchetModel = Percolator.Crypto.Grpc.DoubleRatchetModel;

namespace Percolator.Desktop.Domain.Chat;

public class ChatModel
{
    private readonly DoubleRatchetModelFactory _doubleRatchetModelFactory;
    public IList<MessageModel> Messages => _sortedMessages.Values;
    private readonly SortedList<DateTime,MessageModel> _sortedMessages = new();
    public RemoteClientModel RemoteClientModel { get; }

    public ReadOnlyReactiveProperty<bool> CanReplyIntroduce { get; }
    public Observable<MessageModel> ChatMessage { get; }
    public ReactiveProperty<bool> IntroduceInProgress { get; }= new();

    private readonly Subject<MessageModel> _messageSubject = new();
    private readonly ReactiveProperty<DoubleRatchetModel?> _doubleRatchetModel= new();
    private readonly ECDiffieHellman _ephemeral = ECDiffieHellman.Create();
    private readonly byte[] _ephemeralPublicKey;
    public IReadOnlyCollection<byte> EphemeralPublicKey => _ephemeralPublicKey;

    public ReadOnlyReactiveProperty<bool> CanIntroduce { get; }
    public ByteString Identity => RemoteClientModel.Identity;

    public ChatModel(
        RemoteClientModel remoteClientModel,
        DoubleRatchetModelFactory doubleRatchetModelFactory)
    {
        _doubleRatchetModelFactory = doubleRatchetModelFactory;
        RemoteClientModel = remoteClientModel;
        CanReplyIntroduce = _doubleRatchetModel
            .CombineLatest(RemoteClientModel.SelectedIpAddress, (ephemeral, ip) => (ephemeral, ip))
            .Select(e => e.ephemeral != null && e.ip != null)
            .ToReadOnlyReactiveProperty();
        CanIntroduce = RemoteClientModel.SelectedIpAddress
            .Select(ip => ip != null)
            .ToReadOnlyReactiveProperty();
        ChatMessage = _messageSubject.AsObservable();

        _ephemeralPublicKey = _ephemeral.PublicKey.ToByteArray();
    }

    public void OnChatMessage(MessageModel messageModel)
    {
        _messageSubject.OnNext(messageModel);
    }
    
    public void OnIntroduce(byte[] identity,ECDiffieHellmanPublicKey ephemeral)
    {
        if(_doubleRatchetModel.Value != null)
        {
            return;
        }
        var rootKey = _doubleRatchetModelFactory.CreateRootKey(ephemeral,identity,  _ephemeral);
        _doubleRatchetModel.Value = _doubleRatchetModelFactory.Create(rootKey, _ephemeral);
    }

    public byte[] GetChatData(DateTimeOffset timeStamp, IPAddress selfIp, byte[] identity, string text)
    {
        if(_doubleRatchetModel.Value == null)
        {
            throw new InvalidOperationException("double ratchet model not initialized");
        }
        var associatedData = new AssociatedData
        {
            IdentityKey = ByteString.CopyFrom(identity),
            SourceIp = ByteString.CopyFrom(selfIp.GetAddressBytes()),
            TimeStampUnixUtcMs = timeStamp.ToUnixTimeMilliseconds()
        };
        return _doubleRatchetModel.Value.RatchetEncrypt(Encoding.UTF8.GetBytes(text), associatedData.ToByteArray());
    }

    public bool TryDecrypt(byte[] encrypted,[NotNullWhen(true)] out DecryptResult? result)
    {
        if (_doubleRatchetModel.Value == null)
        {
            result = default;
            return false;
        }
        try
        {
            result = _doubleRatchetModel.Value.Decrypt(encrypted);
            OnChatMessage(new MessageModel(DateTime.Now, Encoding.UTF8.GetString(result.PlainText),false));
            return true;
        }
        catch 
        {
            result = default;
            return false;
        }
    }
}
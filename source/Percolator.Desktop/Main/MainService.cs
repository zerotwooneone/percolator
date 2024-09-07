using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using Google.Protobuf;
using Microsoft.Extensions.Logging;
using Percolator.Crypto;
using Percolator.Desktop.Crypto;
using Percolator.Desktop.Domain.Chat;
using Percolator.Desktop.Domain.Client;
using Percolator.Desktop.Udp;
using Percolator.Desktop.Udp.Interfaces;
using Percolator.Protobuf.Announce;
using Percolator.Protobuf.Introduce;
using R3;
using DoubleRatchetModel = Percolator.Crypto.Grpc.DoubleRatchetModel;

namespace Percolator.Desktop.Main;

public class MainService : IRemoteClientService, IChatService
{
    private readonly UdpClientFactory _udpClientFactory;
    private readonly ILogger<MainService> _logger;
    private readonly ILoggerFactory _loggerFactory;
    private readonly IRemoteClientRepository _remoteClientRepository;
    private readonly ISelfProvider _selfProvider;
    private readonly ChatRepository _chatRepository;
    private readonly DoubleRatchetModelFactory _doubleRatchetModelFactory;
    private readonly ISerializer _serializer;
    private readonly IBroadcaster _broadcaster;
    private readonly IListener _broadcastListener;
    private readonly Observable<Unit> _announceInterval;
    private IDisposable _announceSubscription = new DummyDisposable();
    
    private IListener _introduceListener;
    private readonly ConcurrentBag<IPAddress> _ipBlacklist = new();
    private readonly ConcurrentBag<ByteString> _identityBlacklist = new();
    const int ipMaxBytes = 16;
    
    public MainService(
        UdpClientFactory udpClientFactory,
        ILogger<MainService> logger,
        ILoggerFactory loggerFactory,
        IRemoteClientRepository remoteClientRepository,
        ISelfProvider selfProvider, 
        ChatRepository chatRepository,
        DoubleRatchetModelFactory doubleRatchetModelFactory,
        ISerializer serializer)
    {
        _udpClientFactory = udpClientFactory;
        _logger = logger;
        _loggerFactory = loggerFactory;
        _remoteClientRepository = remoteClientRepository;
        _selfProvider = selfProvider;
        _chatRepository = chatRepository;
        _doubleRatchetModelFactory = doubleRatchetModelFactory;
        _serializer = serializer;
        _broadcaster = _udpClientFactory.CreateBroadcaster(Defaults.DefaultBroadcastPort);
        _broadcastListener = _udpClientFactory.CreateListener(Defaults.DefaultBroadcastPort);
        var ingressContext = new SynchronizationContext();
        _broadcastListener.Received
            .ObserveOn(ingressContext)
            .Subscribe(OnReceivedBroadcast);
    
        _announceInterval = Observable
            .Interval(TimeSpan.FromSeconds(5))  //todo: make configurable()
            .ObserveOn(new SynchronizationContext())
            .Publish()
            .RefCount();

        _introduceListener = _udpClientFactory.CreateListener(Defaults.DefaultIntroducePort);
        _introduceListener.Received 
            .ObserveOn(ingressContext)
            .SubscribeAwait(OnReceivedIntroduceMessage);
    }

    const int SessionIdLength = 8;
    private async ValueTask OnReceivedIntroduceMessage(UdpReceiveResult context, CancellationToken cancellationToken)
    {
        if (_ipBlacklist.Contains(context.RemoteEndPoint.Address))
        {
            _logger.LogWarning("Blacklisted IP received from {Ip}", context.RemoteEndPoint.Address);
            return;
            
        }
        if(context.Buffer == null || context.Buffer.Length == 0)
        {
            _logger.LogWarning("Empty buffer received from {Ip}", context.RemoteEndPoint.Address);
            return;
        }
        
        //todo: make configurable
        const int arbitraryMaxLength =  1000;
        
        if (context.Buffer.Length > arbitraryMaxLength)
        {
            _logger.LogWarning("Buffer received from {Ip} is too large", context.RemoteEndPoint.Address);
            //todo: add to IP ban list
            return;
        }

        var introduce = IntroduceMessage.Parser.ParseFrom(context.Buffer);
        if (introduce.MessageTypeCase == IntroduceMessage.MessageTypeOneofCase.None)
        {
            _logger.LogWarning("Introduce message does not have valid message type");
            //todo: add to IP ban list
            return;
        }

        var currentUtcTime = DateTimeOffset.UtcNow;
        //todo: make this configurable
        var timestampGracePeriod = TimeSpan.FromMinutes(1);
        switch (introduce.MessageTypeCase)
        {
            case IntroduceMessage.MessageTypeOneofCase.IntroduceRequest:
                var payload = introduce.IntroduceRequest.Payload;
                if (payload == null)
                {
                    _logger.LogWarning("Introduce message does not have payload");
                    //todo: add to IP ban list
                    return;
                }

                if (!payload.HasIdentityKey ||
                    payload.IdentityKey.Length == 0)
                {
                    _logger.LogWarning("Introduce message does not have identity key");
                    //todo: add to IP ban list
                    return;
                }

                if (_identityBlacklist.Contains(payload.IdentityKey))
                {
                    _logger.LogWarning("Blacklisted identity received from {Ip}", context.RemoteEndPoint.Address);
                    return;
                }

                if (!payload.HasTimeStampUnixUtcMs ||
                    payload.TimeStampUnixUtcMs < currentUtcTime.Add(-timestampGracePeriod).ToUnixTimeMilliseconds() ||
                    payload.TimeStampUnixUtcMs > currentUtcTime.Add(timestampGracePeriod).ToUnixTimeMilliseconds())
                {
                    _logger.LogWarning("Introduce payload does not have valid timestamp");
                    //todo: add to IP ban list
                    return;
                }

                if (!payload.HasEphemeralKey ||
                    payload.EphemeralKey.Length == 0)
                {
                    _logger.LogWarning("Introduce message does not have ephemeral");
                    //todo: add to IP ban list
                    return;
                }

                if (!payload.HasSourceIp ||
                    payload.SourceIp.Length == 0 ||
                    payload.SourceIp.Length > ipMaxBytes  ||
                    !TryGetIpAddress(payload.SourceIp.ToByteArray(), out var parsedSourceIp) ||
                    !context.RemoteEndPoint.Address.Equals(parsedSourceIp))
                {
                    _logger.LogWarning("Introduce message source ip is invalid");
                    //todo: add to IP ban list
                    return;
                }

                var identity = new RSACryptoServiceProvider()
                {
                    PersistKeyInCsp = false
                };
                identity.ImportRSAPublicKey(payload.IdentityKey.ToByteArray(), out _);

                if (!identity.VerifyData(payload.ToByteArray(),
                        introduce.IntroduceRequest.PayloadSignature.ToByteArray(), HashAlgorithmName.SHA256,
                        RSASignaturePadding.Pkcs1))
                {
                    _logger.LogWarning("payload signature is invalid");
                    return;
                }

                await OnReceivedIntroRequest(context, payload,cancellationToken);
                break;
            case IntroduceMessage.MessageTypeOneofCase.IntroduceReply:
                var proceedPayload = introduce.IntroduceReply.Proceed.Payload;
                if (proceedPayload == null)
                {
                    _logger.LogWarning("Introduce message does not have payload");
                    //todo: add to IP ban list
                    return;
                }

                if (!proceedPayload.HasIdentityKey ||
                    proceedPayload.IdentityKey.Length == 0)
                {
                    _logger.LogWarning("Introduce message does not have identity key");
                    //todo: add to IP ban list
                    return;
                }

                if (_identityBlacklist.Contains(proceedPayload.IdentityKey))
                {
                    _logger.LogWarning("Blacklisted identity received from {Ip}", context.RemoteEndPoint.Address);
                    return;
                }

                if (!proceedPayload.HasTimeStampUnixUtcMs ||
                    proceedPayload.TimeStampUnixUtcMs < currentUtcTime.Add(-timestampGracePeriod).ToUnixTimeMilliseconds() ||
                    proceedPayload.TimeStampUnixUtcMs > currentUtcTime.Add(timestampGracePeriod).ToUnixTimeMilliseconds())
                {
                    _logger.LogWarning("Introduce payload does not have valid timestamp");
                    //todo: add to IP ban list
                    return;
                }

                if (!proceedPayload.HasEphemeralKey ||
                    proceedPayload.EphemeralKey.Length == 0)
                {
                    _logger.LogWarning("Introduce message does not have ephemeral");
                    //todo: add to IP ban list
                    return;
                }
                
                if (!proceedPayload.HasSourceIp ||
                     proceedPayload.SourceIp.Length == 0 ||
                     proceedPayload.SourceIp.Length > ipMaxBytes  ||
                     !TryGetIpAddress(proceedPayload.SourceIp.ToByteArray(), out var proceedSourceIp) ||
                     !context.RemoteEndPoint.Address.Equals(proceedSourceIp))
                {
                    _logger.LogWarning("Introduce reply message source ip is invalid");
                    //todo: add to IP ban list
                    return;
                }

                var proceedIdentity= new RSACryptoServiceProvider()
                {
                    PersistKeyInCsp = false
                };
                proceedIdentity.ImportRSAPublicKey(proceedPayload.IdentityKey.ToByteArray(), out _);

                if (!proceedIdentity.VerifyData(proceedPayload.ToByteArray(),
                        introduce.IntroduceReply.Proceed.PayloadSignature.ToByteArray(), HashAlgorithmName.SHA256,
                        RSASignaturePadding.Pkcs1))
                {
                    _logger.LogWarning("payload signature is invalid");
                    return;
                }

                if (!proceedPayload.HasEphemeralKey || proceedPayload.EphemeralKey.Length == 0)
                {
                    _logger.LogWarning("missing ephemeral key");
                    return;
                }
                
                
                OnReceivedReplyIntro(context, proceedPayload);
                break;
            case IntroduceMessage.MessageTypeOneofCase.ChatMessage:
                var chatMessage = introduce.ChatMessage;
                if (chatMessage == null)
                {
                    _logger.LogWarning("chat message is null");
                    //todo: add to IP ban list
                    return;
                }

                if (!chatMessage.HasData || chatMessage.Data.Length == 0)
                {
                    _logger.LogWarning("chat message does not have data");
                    //todo: add to IP ban list
                    return;
                }

                var chatMessageDataBytes = chatMessage.Data.ToByteArray();
                var unverifiedAdBytes =
                    DoubleRatchetModel.GetUnverifiedAssociatedData(_serializer, chatMessageDataBytes);
                var unverifiedAssociatedData = AssociatedData.Parser.ParseFrom(unverifiedAdBytes);
                if (unverifiedAssociatedData == null)
                {
                    _logger.LogWarning("chat message does not have valid associated data");
                    return;
                }
                if (!unverifiedAssociatedData.HasTimeStampUnixUtcMs ||
                    unverifiedAssociatedData.TimeStampUnixUtcMs < currentUtcTime.Add(-timestampGracePeriod).ToUnixTimeMilliseconds() ||
                    unverifiedAssociatedData.TimeStampUnixUtcMs > currentUtcTime.Add(timestampGracePeriod).ToUnixTimeMilliseconds())
                {
                    _logger.LogWarning("chat message does not have valid timestamp");
                    //todo: add to IP ban list
                    return;
                }
                
                if (!unverifiedAssociatedData.HasSourceIp ||
                    unverifiedAssociatedData.SourceIp.Length == 0 ||
                    unverifiedAssociatedData.SourceIp.Length > ipMaxBytes  ||
                    !TryGetIpAddress(unverifiedAssociatedData.SourceIp.ToByteArray(), out var chatSourceIp) ||
                    !context.RemoteEndPoint.Address.Equals(chatSourceIp))
                {
                    _logger.LogWarning("chat message source ip is invalid");
                    //todo: add to IP ban list
                    return;
                }

                //todo:check for valid rsa key length
                if (!unverifiedAssociatedData.HasIdentityKey || unverifiedAssociatedData.IdentityKey.Length == 0)
                {
                    _logger.LogWarning("chat message does not have a valid identity key");
                    //todo: add to IP ban list
                    return;
                }

                if (!_chatRepository.TryGetByIdentity(unverifiedAssociatedData.IdentityKey, out var chatModel))
                {
                    _logger.LogWarning("chat message identity was not found");
                    return;
                }

                if (!chatModel.TryDecrypt(
                    chatMessageDataBytes,
                    out var decryptResult))
                {
                    _logger.LogWarning("failed to decrypt");
                    return;
                }

                if (decryptResult.PlainText == null || decryptResult.PlainText.Length == 0)
                {
                    return;
                }
                chatModel.OnChatMessage(new MessageModel(DateTime.Now, Encoding.UTF8.GetString(decryptResult.PlainText),false));
                break;
            default:
                _logger.LogWarning("Introduce message does not have valid message type");
                //todo: add to IP ban list
                return;
        }
    }

    private void OnReceivedReplyIntro(UdpReceiveResult context, IntroduceMessage.Types.IntroduceReply.Types.Proceed.Types.Payload payload)
    {
        //todo: make sure this is was actually requested
        var didAdd = false;
        var announcer = _remoteClientRepository.GetOrAdd(payload.IdentityKey, _ =>
        {
            didAdd = true;
            var newModel = new RemoteClientModel(payload.IdentityKey, _loggerFactory.CreateLogger<RemoteClientModel>());
            if (payload.HasPreferredNickname && !string.IsNullOrWhiteSpace(payload.PreferredNickname))
            {
                newModel.PreferredNickname.Value = payload.PreferredNickname.Truncate(maxNicknameLength)!;
            }
            else
            {
                newModel.PreferredNickname.Value = SelfRepository.GetRandomNickname(ByteUtils.GetIntFromBytes(payload.IdentityKey.ToByteArray()));
            }
            return newModel;
        });

        using var announcerSub = _remoteClientRepository.WatchForChanges(announcer);
        announcer.LastSeen.Value = DateTimeOffset.Now;
        announcer.AddIpAddress(context.RemoteEndPoint.Address);
        if (payload.HasPreferredNickname && !string.IsNullOrWhiteSpace(payload.PreferredNickname))
        {
            announcer.PreferredNickname.Value = payload.PreferredNickname.Truncate(maxNicknameLength)!;
        }
        
        if (didAdd)
        {
            if (MessageBox.Show(
                    $"got a reply from {context.RemoteEndPoint.Address} but we didn't request it. Do you want to allow it?",
                    "Warning", MessageBoxButton.YesNo, MessageBoxImage.Exclamation) != MessageBoxResult.Yes)
            { 
                _remoteClientRepository.OnNext(announcer.Identity);
                //todo: remove messagebox to avoid spam
                return; 
            }
        }

        if (!_chatRepository.TryGetByIdentity(announcer.Identity, out var chatModel))
        {
            chatModel = new ChatModel(announcer,_doubleRatchetModelFactory);
            _chatRepository.Add(chatModel);
        }
        chatModel.OnIntroduce(payload.IdentityKey.ToByteArray(),ECDiffieHellmanCngPublicKey.FromByteArray(payload.EphemeralKey.ToByteArray(), CngKeyBlobFormat.EccPublicBlob));
        
        if (didAdd)
        {
            _remoteClientRepository.OnNext(announcer.Identity);
        }
    }

    const int maxNicknameLength = 35;
    private async Task OnReceivedIntroRequest(UdpReceiveResult context,
        IntroduceMessage.Types.IntroduceRequest.Types.Payload payload, CancellationToken cancellationToken)
    {
        var didAdd = false;
        var identityKeyBytes = payload.IdentityKey.ToByteArray();
        var announcer = _remoteClientRepository.GetOrAdd(payload.IdentityKey, _ =>
        {
            didAdd = true;
            var newModel = new RemoteClientModel(payload.IdentityKey, _loggerFactory.CreateLogger<RemoteClientModel>());
            if (payload.HasPreferredNickname && !string.IsNullOrWhiteSpace(payload.PreferredNickname))
            {
                newModel.PreferredNickname.Value = payload.PreferredNickname.Truncate(maxNicknameLength)!;
            }
            else
            {
                newModel.PreferredNickname.Value = SelfRepository.GetRandomNickname(ByteUtils.GetIntFromBytes(identityKeyBytes));
            }
            return newModel;
        });
        using var announcerSub = _remoteClientRepository.WatchForChanges(announcer);
        announcer.AddIpAddress(context.RemoteEndPoint.Address);
        announcer.LastSeen.Value = DateTimeOffset.Now;
        var identity = new RSACryptoServiceProvider
        {
            PersistKeyInCsp = false
        };
        identity.ImportRSAPublicKey(identityKeyBytes, out _);
        if (!_chatRepository.TryGetByIdentity(announcer.Identity, out var chatModel))
        {
            chatModel = new ChatModel(announcer,_doubleRatchetModelFactory);
            _chatRepository.Add(chatModel);
        }
        var ephemeral = ECDiffieHellmanCngPublicKey.FromByteArray(payload.EphemeralKey.ToByteArray(), CngKeyBlobFormat.EccPublicBlob);
        chatModel.OnIntroduce(identityKeyBytes, ephemeral);
        if (payload.HasPreferredNickname && !string.IsNullOrWhiteSpace(payload.PreferredNickname))
        {
            announcer.PreferredNickname.Value = payload.PreferredNickname.Truncate(maxNicknameLength)!;
        }

        if (didAdd)
        {
            _remoteClientRepository.OnNext(announcer.Identity);
        }

        if (_selfProvider.GetSelf().AutoReplyIntroductions.CurrentValue)
        {
            await TrySendReplyIntroduction(chatModel, cancellationToken);
        }
    }

    private void OnReceivedBroadcast(UdpReceiveResult context)
    {
        if (_ipBlacklist.Contains(context.RemoteEndPoint.Address))
        {
            _logger.LogWarning("Blacklisted IP received from {Ip}", context.RemoteEndPoint.Address);
            return;
            
        }
        if(context.Buffer == null || context.Buffer.Length == 0)
        {
            _logger.LogWarning("Empty buffer received from {Ip}", context.RemoteEndPoint.Address);
            return;
        }
        
        //todo: make configurable
        const int arbitraryMaxLength =  400;
        
        if (context.Buffer.Length > arbitraryMaxLength)
        {
            _logger.LogWarning("Buffer received from {Ip} is too large", context.RemoteEndPoint.Address);
            //todo: add to IP ban list
            return;
        }
        var announce = AnnounceMessage.Parser.ParseFrom(context.Buffer);
        if (announce.MessageTypeCase == AnnounceMessage.MessageTypeOneofCase.None)
        {
            _logger.LogWarning("Announce message does not have valid message type");
            //todo: add to IP ban list
            return;
        }

        var currentUtcTime = DateTimeOffset.UtcNow;
        switch (announce.MessageTypeCase)
        {
            case AnnounceMessage.MessageTypeOneofCase.Identity:
                var identityMessage = announce.Identity;
                var identityPayload = identityMessage.Payload;
                if (identityMessage == null || identityPayload == null)
                {
                    _logger.LogWarning("Announce message does not have valid payload");
                    return;
                }

                if (!identityPayload.HasIdentityKey ||
                    identityPayload.IdentityKey.Length == 0 ||
                    identityPayload.IdentityKey.Length > arbitraryMaxLength)
                {
                    _logger.LogWarning("Announce message does not have valid identity key");
                    return;
                }
                
                if (_identityBlacklist.Contains(identityPayload.IdentityKey))
                {
                    _logger.LogWarning("Blacklisted identity received from {Ip}", context.RemoteEndPoint.Address);
                    return;
                }
                
                if (identityPayload.IdentityKey.Equals(
                        ByteString.CopyFrom(_selfProvider.GetSelf().Identity.ExportRSAPublicKey())))
                {
                    return;
                }

                //todo: make this configurable
                var timestampGracePeriod = TimeSpan.FromMinutes(1);
                
                if (!identityPayload.HasTimeStampUnixUtcMs ||
                    identityPayload.TimeStampUnixUtcMs > currentUtcTime.Add(timestampGracePeriod).ToUnixTimeMilliseconds() ||
                    identityPayload.TimeStampUnixUtcMs < currentUtcTime.Add(-timestampGracePeriod).ToUnixTimeMilliseconds())
                {
                    _logger.LogWarning("Announce message timestamp is invalid");
                    //todo: add to IP ban list
                    return;
                }

                if (!identityMessage.HasPayloadSignature || 
                    identityMessage.PayloadSignature.Length == 0 ||
                    identityMessage.PayloadSignature.Length > arbitraryMaxLength)
                {
                    _logger.LogWarning("Announce message does not have a payload signature");
                    return;
                }

                if (identityPayload.HasPort &&
                    identityPayload.Port <= 0)
                {
                    _logger.LogWarning("Announce message does not have a valid port");
                    return;
                }
                
                if (!identityPayload.HasSourceIp ||
                    identityPayload.SourceIp.Length == 0 ||
                    identityPayload.SourceIp.Length > ipMaxBytes ||
                    !TryGetIpAddress(identityPayload.SourceIp.ToByteArray(), out var parsedSourceIp) ||
                    !context.RemoteEndPoint.Address.Equals(parsedSourceIp))
                {
                    _logger.LogWarning("broadcast message source ip is invalid");
                    //todo: add to IP ban list
                    return;
                }

                if (identityPayload.HasPreferredNickname &&
                    identityPayload.PreferredNickname.Length > maxNicknameLength)
                {
                    _logger.LogWarning("Announce message does not have a valid preferred nickname");
                    return;
                }
                OnIdentityBroadcast(identityMessage, context);
                break;
            default:
                _logger.LogError("Announce message does not have valid message type: {MessageType}", announce.MessageTypeCase);
                return;
        }
    }
    
    private bool TryGetIpAddress(byte[] ipBytes, [NotNullWhen(true)] out IPAddress? ipAddress)
    {
        try
        {
            ipAddress = new IPAddress(ipBytes);
            return true;
        }
        catch (ArgumentException)
        {
            ipAddress = null;
            return false;
        }
    }

    private void OnIdentityBroadcast(AnnounceMessage.Types.Identity identityMessage, UdpReceiveResult context)
    {
        using var identity = new RSACryptoServiceProvider();
        identity.PersistKeyInCsp = false;
        var identityBytes = identityMessage.Payload.IdentityKey.ToByteArray();
        identity.ImportRSAPublicKey(identityBytes, out _);
        var payloadBytes = identityMessage.Payload.ToByteArray();
        if(!identity.VerifyData(payloadBytes, identityMessage.PayloadSignature.ToByteArray(), 
               HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1))
        {
            _logger.LogWarning("Announce message does not have valid payload signature");
            //todo: add to IP ban list
            return;
        }

        var didAdd = false;
        var announcerModel = _remoteClientRepository.GetOrAdd(identityMessage.Payload.IdentityKey,_=>
        {
            didAdd = true;
            return new RemoteClientModel(
                identityMessage.Payload.IdentityKey,
                _loggerFactory.CreateLogger<RemoteClientModel>());
        });
        using var announcerSub = _remoteClientRepository.WatchForChanges(announcerModel);
        announcerModel.AddIpAddress(context.RemoteEndPoint.Address);
        announcerModel.LastSeen.Value= DateTimeOffset.Now;
        if (identityMessage.Payload.HasPreferredNickname)
        {
            const int maxNicknameLength=140;
            var preferredNickname = identityMessage.Payload.PreferredNickname;
            var identityBase64 = identityMessage.Payload.IdentityKey.ToBase64();
            var nextNick = preferredNickname.Length ==0 
                ? identityBase64.Substring(0,Math.Min(maxNicknameLength, identityBase64.Length)) 
                :preferredNickname.Substring(0, Math.Min( maxNicknameLength, preferredNickname.Length));
            
            announcerModel.PreferredNickname.Value = nextNick;
        }
        if (identityMessage.Payload.HasPort)
        {
            announcerModel.Port.Value = identityMessage.Payload.Port;
        }

        if (didAdd)
        {
            _remoteClientRepository.OnNext(identityMessage.Payload.IdentityKey);
        }
    }

    public void Announce()
    {
        _announceSubscription.Dispose();
        _announceSubscription = _announceInterval
            .SubscribeAwait(async (_,c) =>
            {
                if (!TryGetIpAddress(out var sourceIp))
                {
                    return;
                }
                await _broadcaster.Broadcast(GetAnnounceIdentityBytes(DateTimeOffset.Now, sourceIp),c);
            });
    }

    private byte[] GetAnnounceIdentityBytes(
        DateTimeOffset currentTime,
        IPAddress sourceIp,
        int? handshakePort=null)
    {
        var selfModel = _selfProvider.GetSelf();
        var payload = new AnnounceMessage.Types.Identity.Types.Payload()
        {
            IdentityKey = ByteString.CopyFrom(selfModel.Identity.ExportRSAPublicKey()),
            TimeStampUnixUtcMs = currentTime.ToUniversalTime().ToUnixTimeMilliseconds(),
            PreferredNickname = selfModel.PreferredNickname.Value,
            SourceIp = ByteString.CopyFrom(sourceIp.GetAddressBytes()),
        };
        if (handshakePort != null && handshakePort != Defaults.DefaultIntroducePort)
        {
            payload.Port = handshakePort.Value;
        }
        var payloadBytes = payload.ToByteArray();
        var announceBytes = new AnnounceMessage
        {
            Identity = new AnnounceMessage.Types.Identity
            {
                Payload = payload,
                PayloadSignature = ByteString.CopyFrom(selfModel.Identity.SignData(
                    payloadBytes,
                    HashAlgorithmName.SHA256,
                    RSASignaturePadding.Pkcs1
                ))
            }
        }.ToByteArray();
            
        return announceBytes;
    }

    public bool TryGetIpAddress([NotNullWhen(true)] out IPAddress? localIp)
    {
        try
        {
            using (Socket socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, 0))
            {
                socket.Connect("8.8.8.8", 65530);
                if (socket.LocalEndPoint is not IPEndPoint endPoint)
                {
                    localIp = null;
                    return false;
                }
                localIp = endPoint.Address;
                return true;
            }
        }
        catch (SocketException socketException)
        {
            localIp = null;
            return false;
        }
    }

    public void StopAnnounce()
    {
        _announceSubscription.Dispose();
    }

    public void ListenForIntroductions()
    {
        _introduceListener.IsListening.Value = true;
    }

    public void StopListeningForIntroductions()
    {
        _introduceListener.IsListening.Value = false;
    }

    public void ListenForBroadcasts()
    {
        _broadcastListener.IsListening.Value = true;
    }

    public void StopListeningForBroadcasts()
    {
        _broadcastListener.IsListening.Value = false;
    }
    
    public async Task<bool> TrySendIntroduction(ChatModel chatModel,
        CancellationToken cancellationToken = default)
    {
        if (chatModel.RemoteClientModel.SelectedIpAddress.CurrentValue == null)
        {
            _logger.LogWarning("ip address not selected for introduction");
            return false;
        }

        return await TrySendIntroduction(
            chatModel,
            chatModel.RemoteClientModel.SelectedIpAddress.CurrentValue,
            chatModel.RemoteClientModel.Port.Value,
            cancellationToken);
    }
    
    private async Task<bool> TrySendIntroduction(
        ChatModel chatModel,
        IPAddress destinationIp, 
        int? port=null,
        CancellationToken cancellationToken = default)
    {
        if (!TryGetIpAddress(out var sourceIp))
        {
            _logger.LogWarning("unable to get self ip");
            return false;
        }
        var udpClient = _udpClientFactory.GetOrCreateSender(port ?? Defaults.DefaultIntroducePort);
        var ephemeralPublicKey = chatModel.EphemeralPublicKey as byte[] ?? chatModel.EphemeralPublicKey.ToArray();
        await udpClient.Send(destinationIp, GetIntroduceRequestBytes(DateTimeOffset.Now, sourceIp,ephemeralPublicKey), cancellationToken);

        return true;
    }

    public async Task<bool> TrySendReplyIntroduction(ChatModel chatModel, 
        CancellationToken cancellationToken = default)
    {
        if (!TryGetIpAddress(out var selfIp))
        {
            _logger.LogWarning("unable to get self ip");
            return false;
        }

        if (!chatModel.CanReplyIntroduce.CurrentValue)
        {
            _logger.LogWarning("cannot send reply introduction");
            return false;
        }
        var udpClient = _udpClientFactory.GetOrCreateSender(chatModel.RemoteClientModel.Port.Value);
        
        var curretTime = DateTimeOffset.Now;
        
        //todo:consider sending multiple copies
        await udpClient.Send(chatModel.RemoteClientModel.SelectedIpAddress.CurrentValue!, GetIntroReplyBytes(
            curretTime,
            selfIp,
            chatModel.EphemeralPublicKey as byte[] ?? chatModel.EphemeralPublicKey.ToArray()), cancellationToken);
        
        return true;
    }

    private byte[] GetIntroReplyBytes(DateTimeOffset currentTime,
        IPAddress sourceIp,
        byte[] ephemeralKey)
    {
        var payload = new IntroduceMessage.Types.IntroduceReply.Types.Proceed.Types.Payload
        {
            IdentityKey =ByteString.CopyFrom( _selfProvider.GetSelf().Identity.ExportRSAPublicKey()),
            EphemeralKey = ByteString.CopyFrom(ephemeralKey),
            TimeStampUnixUtcMs = currentTime.ToUniversalTime().ToUnixTimeMilliseconds(),
            SourceIp = ByteString.CopyFrom(sourceIp.GetAddressBytes()),
            PreferredNickname = _selfProvider.GetSelf().PreferredNickname.Value
        };
        var payloadBytes = payload.ToByteArray();

        var m = new IntroduceMessage
        {
            IntroduceReply = new IntroduceMessage.Types.IntroduceReply
            {
                Proceed = new IntroduceMessage.Types.IntroduceReply.Types.Proceed()
                {
                    Payload = payload,
                    PayloadSignature = ByteString.CopyFrom(_selfProvider.GetSelf().Identity.SignData(payloadBytes,
                        HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)),
                }
            }
        };
        return m.ToByteArray();
    }
    
    private byte[] GetIntroduceRequestBytes(DateTimeOffset currentTime, IPAddress sourceIp, byte[] ephemeralPublicKey)
    {
        var payload = new IntroduceMessage.Types.IntroduceRequest.Types.Payload
        {
            IdentityKey =ByteString.CopyFrom( _selfProvider.GetSelf().Identity.ExportRSAPublicKey()),
            EphemeralKey = ByteString.CopyFrom(ephemeralPublicKey),
            TimeStampUnixUtcMs = currentTime.ToUniversalTime().ToUnixTimeMilliseconds(),
            SourceIp = ByteString.CopyFrom(sourceIp.GetAddressBytes())
        };
        var payloadBytes = payload.ToByteArray();

        var m = new IntroduceMessage
        {
            IntroduceRequest = new IntroduceMessage.Types.IntroduceRequest
            {
                Payload = payload,
                PayloadSignature = ByteString.CopyFrom(_selfProvider.GetSelf().Identity.SignData(payloadBytes,
                    HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)),
            }
        };
        return m.ToByteArray();
    }

    public async Task SendChatMessage(ChatModel chatModel, string text,
        CancellationToken cancellationToken = default)
    {
        var udpClient = _udpClientFactory.GetOrCreateSender(chatModel.RemoteClientModel.Port.Value);
        
        var curretTime = DateTimeOffset.Now;

        await udpClient.Send(chatModel.RemoteClientModel.SelectedIpAddress.CurrentValue!, await GetChatBytes(
            curretTime,
            chatModel,
            text), 
            cancellationToken);
    }

    private async Task< byte[]> GetChatBytes(DateTimeOffset curretTime, ChatModel chatModel,string text)
    {
        if (!TryGetIpAddress(out var selfIp))
        {
            throw new InvalidOperationException("Failed to get ip address");
        }

        if (!chatModel.CanReplyIntroduce.CurrentValue)
        {
            throw new InvalidOperationException("must have double ratchet");
        }
        
        return new IntroduceMessage
        {
            ChatMessage = new IntroduceMessage.Types.ChatMessage
            {
                Data = ByteString.CopyFrom(chatModel.GetChatData(curretTime,selfIp,_selfProvider.GetSelf().Identity.ExportRSAPublicKey(),text))
            }
        }.ToByteArray();
    }
}

internal class DummyDisposable : IDisposable
{
    public void Dispose()
    {
        
    }
}
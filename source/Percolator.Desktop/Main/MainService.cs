using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Windows;
using Google.Protobuf;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Percolator.Desktop.Crypto;
using Percolator.Desktop.Udp;
using Percolator.Desktop.Udp.Interfaces;
using Percolator.Protobuf.Announce;
using Percolator.Protobuf.Introduce;
using R3;

namespace Percolator.Desktop.Main;

public class MainService : IAnnouncerService, IChatService,IAnnouncerInitializer
{
    private readonly UdpClientFactory _udpClientFactory;
    private readonly ILogger<MainService> _logger;
    private readonly SelfEncryptionService _selfEncryptionService;
    private readonly ILoggerFactory _loggerFactory;
    private readonly IServiceScopeFactory _serviceScopeFactory;
    private readonly IBroadcaster _broadcaster;
    private readonly IListener _broadcastListener;
    private readonly Observable<Unit> _announceInterval;
    private IDisposable _announceSubscription = new DummyDisposable();
    private readonly ConcurrentDictionary<ByteString, AnnouncerModel> _announcersByIdentity= new();
    public ReactiveProperty<string> PreferredNickname { get; }
    
    public Observable<ByteString> AnnouncerAdded => _announcerAdded;
    private Subject<ByteString> _announcerAdded = new();
    private IListener _introduceListener;
    private readonly ConcurrentBag<IPAddress> _ipBlacklist = new();
    private readonly ConcurrentBag<ByteString> _identityBlacklist = new();
    private ConcurrentDictionary<ByteString,AnnouncerModel> _announcersBySessionId= new();
    public ReactiveProperty<bool> BroadcastListen { get; } = new();
    const int ipMaxBytes = 16;

    public MainService(
        UdpClientFactory udpClientFactory,
        ILogger<MainService> logger,
        SelfEncryptionService selfEncryptionService,
        ILoggerFactory loggerFactory,
        IServiceScopeFactory serviceScopeFactory)
    {
        _udpClientFactory = udpClientFactory;
        _logger = logger;
        _selfEncryptionService = selfEncryptionService;
        _loggerFactory = loggerFactory;
        _serviceScopeFactory = serviceScopeFactory;
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

        PreferredNickname = new ReactiveProperty<string>(GetRandomNickname(GetIntFromBytes(_selfEncryptionService.Identity.ExportRSAPublicKey())));
        _introduceListener = _udpClientFactory.CreateListener(Defaults.DefaultIntroducePort);
        _introduceListener.Received 
            .ObserveOn(ingressContext)
            .SubscribeAwait(OnReceivedIntroduce);

        ListenForIntroductions.Subscribe(b => { _introduceListener.IsListening.Value = b; });
        BroadcastListen.Subscribe(b =>
        {
            _broadcastListener.IsListening.Value = b;
        });
    }

    private int GetIntFromBytes(byte[] bytes)
    {
        var resultBytes =new byte[]{3,7,11,15};
        const int Prime = 16777619;
        for (int i = 0; i < resultBytes.Length; i++)
        {
            resultBytes[i%4] = (byte)((unchecked(bytes[i] ^ resultBytes[i % 4] * Prime)) % 255);
        }
        return BitConverter.ToInt32(resultBytes, 0);
    }

    private string GetRandomNickname(int? seed=null)
    {
        var random = seed is null 
            ? new Random() 
            : new Random(seed.Value);
        var numberOfChars = random.Next(9, 20);
        var vowels = new[] {'a', 'A', '4', '@', '^', 'e', 'E', '3', 'i', 'I', '1','o', 'O', '0', 'u', 'U', 'y', 'Y'};
        var consonants = new[]{'b', 'c', 'd', 'f', 'g', 'h', 'j', 'k', 'l', 'm', 'n', 'p', 'q', 'r', 's', 't', 'v', 'w', 'x', 'z','B', 'C','(', 'D', 'F', 'G', 'H','#','8', 'J', 'K', 'L','7', 'M', 'N', 'P', 'Q', 'R', 'S','$', 'T', 'V', 'W', 'X', 'Z'};
        var list = new List<char>(numberOfChars*2);
        var isVowel = random.Next(1,1001) %2 == 0;
        do
        {
            var newPart = isVowel
                ? Enumerable.Range(0, random.Next(1, 3)).Select(_ => vowels[random.Next(0, vowels.Length)])
                : new[] {consonants[random.Next(0, consonants.Length)]};
            list.AddRange(newPart);
            isVowel = !isVowel;
        } while (list.Count < numberOfChars);

        return new string(list.ToArray());
    }
    const int SessionIdLength = 8;
    private async ValueTask OnReceivedIntroduce(UdpReceiveResult context, CancellationToken cancellationToken)
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

        var introduce = IntroduceRequest.Parser.ParseFrom(context.Buffer);
        if (introduce.MessageTypeCase == IntroduceRequest.MessageTypeOneofCase.None)
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
            case IntroduceRequest.MessageTypeOneofCase.UnknownPublicKey:
                var payload = introduce.UnknownPublicKey.Payload;
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
                        introduce.UnknownPublicKey.PayloadSignature.ToByteArray(), HashAlgorithmName.SHA256,
                        RSASignaturePadding.Pkcs1))
                {
                    _logger.LogWarning("payload signature is invalid");
                    return;
                }

                await OnReceivedUnknownPublicKey(context, payload,cancellationToken);
                break;
            case IntroduceRequest.MessageTypeOneofCase.IntroduceReply:
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
                if (!proceedPayload.HasIv || proceedPayload.Iv.Length == 0)
                {
                    _logger.LogWarning("missing iv");
                    return;
                }
                if (!proceedPayload.HasEncryptedSessionKey || proceedPayload.EncryptedSessionKey.Length == 0)
                {
                    _logger.LogWarning("missing encrypted session key");
                    return;
                }
                
                
                OnReceivedReplyIntro(context, proceedPayload);
                break;
            case IntroduceRequest.MessageTypeOneofCase.ChatMessage:
                var chatMessage = introduce.ChatMessage;
                if (chatMessage == null)
                {
                    _logger.LogWarning("chat message is null");
                    //todo: add to IP ban list
                    return;
                }

                if (chatMessage.Signed == null)
                {
                    _logger.LogWarning("chat message does not have signed");
                    //todo: add to IP ban list
                    return;
                }

                if (!chatMessage.HasSignedSignature || chatMessage.SignedSignature.Length == 0)
                {
                    _logger.LogWarning("chat message does not have signed signature");
                    //todo: add to IP ban list
                    return;
                }
                if (!chatMessage.Signed.HasTimeStampUnixUtcMs ||
                     chatMessage.Signed.TimeStampUnixUtcMs < currentUtcTime.Add(-timestampGracePeriod).ToUnixTimeMilliseconds() ||
                     chatMessage.Signed.TimeStampUnixUtcMs > currentUtcTime.Add(timestampGracePeriod).ToUnixTimeMilliseconds())
                {
                    _logger.LogWarning("Introduce payload does not have valid timestamp");
                    //todo: add to IP ban list
                    return;
                }
                
                if (!chatMessage.Signed.HasSourceIp ||
                    chatMessage.Signed.SourceIp.Length == 0 ||
                    chatMessage.Signed.SourceIp.Length > ipMaxBytes  ||
                    !TryGetIpAddress(chatMessage.Signed.SourceIp.ToByteArray(), out var chatSourceIp) ||
                    !context.RemoteEndPoint.Address.Equals(chatSourceIp))
                {
                    _logger.LogWarning("chat message source ip is invalid");
                    //todo: add to IP ban list
                    return;
                }

                if (!chatMessage.Signed.HasSessionKeyId || chatMessage.Signed.SessionKeyId.Length != 8)
                {
                    _logger.LogWarning("chat message does not have a valid session key id");
                    //todo: add to IP ban list
                    return;
                }

                if (!_announcersBySessionId.TryGetValue(chatMessage.Signed.SessionKeyId, out var announcer))
                {
                    _logger.LogWarning("chat message session key id was not found");
                    //todo: add to IP ban list
                    return;
                }

                if (!announcer.Ephemeral.Value.VerifyData(chatMessage.Signed.ToByteArray(),
                        chatMessage.SignedSignature.ToByteArray(), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1))
                {
                    _logger.LogWarning("chat message signature is invalid");
                    return;
                }

                var payloadBytes =
                    await announcer.SessionKey.Value.NaiveDecrypt(chatMessage.Signed.EncryptedPayload.ToByteArray());
                var chatPayload =
                    IntroduceRequest.Types.ChatMessage.Types.Signed.Types.Payload.Parser.ParseFrom(payloadBytes);
                if (!chatPayload.HasMessage || string.IsNullOrWhiteSpace(chatPayload.Message))
                {
                    return;
                }
                OnReceivedChatMessage(context, chatMessage,announcer, chatPayload);
                break;
            default:
                _logger.LogWarning("Introduce message does not have valid message type");
                //todo: add to IP ban list
                return;
        }
    }

    private void OnReceivedChatMessage(UdpReceiveResult context, IntroduceRequest.Types.ChatMessage chatMessage,
        AnnouncerModel announcer, IntroduceRequest.Types.ChatMessage.Types.Signed.Types.Payload chatPayload)
    {
        announcer.OnChatMessage(new MessageModel(DateTime.Now, chatPayload.Message,false));
    }

    private void OnReceivedReplyIntro(UdpReceiveResult context, IntroduceRequest.Types.IntroduceReply.Types.Proceed.Types.Payload payload)
    {
        var didAdd = false;
        var announcer = _announcersByIdentity.GetOrAdd(payload.IdentityKey, _ =>
        {
            didAdd = true;
            var newModel = new AnnouncerModel(payload.IdentityKey, _loggerFactory.CreateLogger<AnnouncerModel>());
            if (payload.HasPreferredNickname && !string.IsNullOrWhiteSpace(payload.PreferredNickname))
            {
                newModel.PreferredNickname.Value = payload.PreferredNickname.Truncate(maxNicknameLength)!;
            }
            else
            {
                newModel.PreferredNickname.Value = GetRandomNickname(GetIntFromBytes(payload.IdentityKey.ToByteArray()));
            }
            return newModel;
        });

        using var announcerSub = WatchForChanges(announcer);
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
                _announcerAdded.OnNext(announcer.Identity);
                //todo: remove messagebox to avoid spam
                return; 
            }
        }

        var ephemeral = new RSACryptoServiceProvider
        {
            PersistKeyInCsp = false
        };
        ephemeral.ImportRSAPublicKey(payload.EphemeralKey.ToByteArray(), out _);
        announcer.Ephemeral.Value = ephemeral;
        
        Aes aes = Aes.Create();
        RSAOAEPKeyExchangeDeformatter keyDeformatter = new RSAOAEPKeyExchangeDeformatter(_selfEncryptionService.Ephemeral);
        var encryptedSessionKey = payload.EncryptedSessionKey.ToByteArray();
        aes.Key = keyDeformatter.DecryptKeyExchange(encryptedSessionKey);
        aes.IV= payload.Iv.ToArray();
        announcer.SessionKey.Value = aes;

        var sessionId = GetSessionId(encryptedSessionKey);
        _announcersBySessionId.TryAdd(sessionId, announcer);
        announcer.SessionId.Value = sessionId;
        
        if (didAdd)
        {
            _announcerAdded.OnNext(announcer.Identity);
        }
    }

    private IDisposable WatchForChanges(AnnouncerModel announcer)
    {
        return announcer.PreferredNickname
                .Skip(1)
                .Take(1)
                .Select(_=>Unit.Default)
                .Amb(announcer.LastSeen
                    .Skip(1)
                    .Take(1)
                    .Select(_=>Unit.Default))
                .SelectAwait(async (_, _) =>
                {
                    using var scope = _serviceScopeFactory.CreateScope();
                    var persistenceService = scope.ServiceProvider.GetRequiredService<IPersistenceService>();
                    await persistenceService.UpdateAnnouncer(announcer.Identity, announcer.PreferredNickname.Value,announcer.LastSeen.Value);
                    return Unit.Default;
                })
            .Subscribe();
    }

    const int maxNicknameLength = 35;
    private async Task OnReceivedUnknownPublicKey(UdpReceiveResult context,
        IntroduceRequest.Types.UnknownPublicKey.Types.Payload payload, CancellationToken cancellationToken)
    {
        var didAdd = false;
        var announcer = _announcersByIdentity.GetOrAdd(payload.IdentityKey, _ =>
        {
            didAdd = true;
            var newModel = new AnnouncerModel(payload.IdentityKey, _loggerFactory.CreateLogger<AnnouncerModel>());
            if (payload.HasPreferredNickname && !string.IsNullOrWhiteSpace(payload.PreferredNickname))
            {
                newModel.PreferredNickname.Value = payload.PreferredNickname.Truncate(maxNicknameLength)!;
            }
            else
            {
                newModel.PreferredNickname.Value = GetRandomNickname(GetIntFromBytes(payload.IdentityKey.ToByteArray()));
            }
            return newModel;
        });
        using var announcerSub = WatchForChanges(announcer);
        announcer.AddIpAddress(context.RemoteEndPoint.Address);
        announcer.LastSeen.Value = DateTimeOffset.Now;
        var ephemeral = new RSACryptoServiceProvider
        {
            PersistKeyInCsp = false
        };
        ephemeral.ImportRSAPublicKey(payload.EphemeralKey.ToByteArray(), out _);
        announcer.Ephemeral.Value = ephemeral;
        if (payload.HasPreferredNickname && !string.IsNullOrWhiteSpace(payload.PreferredNickname))
        {
            announcer.PreferredNickname.Value = payload.PreferredNickname.Truncate(maxNicknameLength)!;
        }

        if (didAdd)
        {
            _announcerAdded.OnNext(announcer.Identity);
        }

        if (AutoReplyIntroductions.CurrentValue)
        {
            if (!TryGetIpAddress(out var sourceIp))
            {
                _logger.LogWarning("Failed to get own ip address");
                return;
            }
            await SendReplyIntroduction(announcer, sourceIp, cancellationToken);
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
                        ByteString.CopyFrom(_selfEncryptionService.Identity.ExportRSAPublicKey())))
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
        var announcerModel = _announcersByIdentity.GetOrAdd(identityMessage.Payload.IdentityKey,_=>
        {
            didAdd = true;
            return new AnnouncerModel(
                identityMessage.Payload.IdentityKey,
                _loggerFactory.CreateLogger<AnnouncerModel>());
        });
        using var announcerSub = WatchForChanges(announcerModel);
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
            _announcerAdded.OnNext(identityMessage.Payload.IdentityKey);
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
        var payload = new AnnounceMessage.Types.Identity.Types.Payload()
        {
            IdentityKey = ByteString.CopyFrom(_selfEncryptionService.Identity.ExportRSAPublicKey()),
            TimeStampUnixUtcMs = currentTime.ToUniversalTime().ToUnixTimeMilliseconds(),
            PreferredNickname = PreferredNickname.Value,
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
                PayloadSignature = ByteString.CopyFrom(_selfEncryptionService.Identity.SignData(
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

    public IReadOnlyDictionary<ByteString, AnnouncerModel> Announcers => _announcersByIdentity;
    public ReactiveProperty<bool> AutoReplyIntroductions { get; } = new();
    public ReactiveProperty<bool> ListenForIntroductions { get; } = new();

    public async Task SendIntroduction(IPAddress destination, 
        int port, 
        IPAddress sourceIp,
        CancellationToken cancellationToken = default)
    {
        var udpClient = _udpClientFactory.GetOrCreateSender(port);
        await udpClient.Send(destination, GetUnknownPublicKeyBytes(DateTimeOffset.Now, sourceIp), cancellationToken);
    }

    public async Task SendReplyIntroduction(AnnouncerModel announcerModel, 
        IPAddress sourceIp,
        CancellationToken cancellationToken = default)
    {
        var udpClient = _udpClientFactory.GetOrCreateSender(announcerModel.Port.Value);
        
        //todo:try to reuse keys that failed to send
        Aes aes = Aes.Create();
        
        var keyFormatter = new RSAOAEPKeyExchangeFormatter(announcerModel.Ephemeral.Value!);
        var encryptedSessionKey = keyFormatter.CreateKeyExchange(aes.Key, typeof(Aes));
        
        var curretTime = DateTimeOffset.Now;

        var sessionId = GetSessionId(encryptedSessionKey);
        
        //todo:remove old session keys
        _announcersBySessionId.TryAdd(sessionId, announcerModel);
        announcerModel.SessionId.Value = sessionId;
        
        //todo:consider sending multiple copies
        await udpClient.Send(announcerModel.SelectedIpAddress.CurrentValue!, GetIntroReplyBytes(
            curretTime,
            aes,
            sourceIp,
            encryptedSessionKey), cancellationToken);
        
        announcerModel.SessionKey.Value = aes;
    }

    private ByteString GetSessionId(byte[] encryptedSessionKey)
    {
        byte[] bytes = encryptedSessionKey.Take(2)
            .Concat(encryptedSessionKey.Skip(3).Take(3).Concat(encryptedSessionKey.Skip(7)))
            .ToArray();
        var md5 = MD5.Create();
        var hash = md5.ComputeHash(bytes);
        
        const int Prime = 16777619;
        var result = new byte[SessionIdLength]{3,7,11,15,19,23,27,31};
        
        for (int startIndex = 0; startIndex < hash.Length; startIndex+=SessionIdLength)
        {
            for (int i = 0; i < SessionIdLength; i++)
            {
                result[i] = (byte) ((unchecked((hash[i+startIndex] ^ result[i])*Prime))%255);
            }
        }

        return ByteString.CopyFrom(result);
    }

    private byte[] GetIntroReplyBytes(DateTimeOffset currentTime,
        Aes aes, 
        IPAddress sourceIp,
        byte[] encryptedSessionKey)
    {
        var payload = new IntroduceRequest.Types.IntroduceReply.Types.Proceed.Types.Payload
        {
            IdentityKey =ByteString.CopyFrom( _selfEncryptionService.Identity.ExportRSAPublicKey()),
            EphemeralKey = ByteString.CopyFrom(_selfEncryptionService.Ephemeral.ExportRSAPublicKey()),
            TimeStampUnixUtcMs = currentTime.ToUniversalTime().ToUnixTimeMilliseconds(),
            Iv = ByteString.CopyFrom(aes.IV),
            EncryptedSessionKey = ByteString.CopyFrom(encryptedSessionKey),
            SourceIp = ByteString.CopyFrom(sourceIp.GetAddressBytes())
        };
        var payloadBytes = payload.ToByteArray();

        var m = new IntroduceRequest
        {
            IntroduceReply = new IntroduceRequest.Types.IntroduceReply
            {
                Proceed = new IntroduceRequest.Types.IntroduceReply.Types.Proceed()
                {
                    Payload = payload,
                    PayloadSignature = ByteString.CopyFrom(_selfEncryptionService.Identity.SignData(payloadBytes,
                        HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)),
                }
            }
        };
        return m.ToByteArray();
    }
    
    private byte[] GetUnknownPublicKeyBytes(DateTimeOffset currentTime, IPAddress sourceIp)
    {
        var payload = new IntroduceRequest.Types.UnknownPublicKey.Types.Payload
        {
            IdentityKey =ByteString.CopyFrom( _selfEncryptionService.Identity.ExportRSAPublicKey()),
            EphemeralKey = ByteString.CopyFrom(_selfEncryptionService.Ephemeral.ExportRSAPublicKey()),
            TimeStampUnixUtcMs = currentTime.ToUniversalTime().ToUnixTimeMilliseconds(),
            SourceIp = ByteString.CopyFrom(sourceIp.GetAddressBytes())
        };
        var payloadBytes = payload.ToByteArray();

        var m = new IntroduceRequest
        {
            UnknownPublicKey = new IntroduceRequest.Types.UnknownPublicKey
            {
                Payload = payload,
                PayloadSignature = ByteString.CopyFrom(_selfEncryptionService.Identity.SignData(payloadBytes,
                    HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)),
            }
        };
        return m.ToByteArray();
    }

    public async Task SendChatMessage(AnnouncerModel announcerModel, string text,
        CancellationToken cancellationToken = default)
    {
        var udpClient = _udpClientFactory.GetOrCreateSender(announcerModel.Port.Value);
        
        var curretTime = DateTimeOffset.Now;

        await udpClient.Send(announcerModel.SelectedIpAddress.CurrentValue!, await GetChatBytes(
            curretTime,
            announcerModel,
            text), 
            cancellationToken);
    }

    private async Task< byte[]> GetChatBytes(DateTimeOffset curretTime, AnnouncerModel announcerModel,string text)
    {
        if (!TryGetIpAddress(out var selfIp))
        {
            throw new InvalidOperationException("Failed to get ip address");
        }

        if (announcerModel.SessionKey.Value == null)
        {
            throw new InvalidOperationException("must have session key");
        }

        var payload = new IntroduceRequest.Types.ChatMessage.Types.Signed.Types.Payload
        {
            Message = text
        };
        var naiveEncrypt = await announcerModel.SessionKey.Value.NaiveEncrypt(payload.ToByteArray());
        var signed = new IntroduceRequest.Types.ChatMessage.Types.Signed
        {
            SourceIp = ByteString.CopyFrom(selfIp.GetAddressBytes()),
            EncryptedPayload = ByteString.CopyFrom(naiveEncrypt),
            SessionKeyId = announcerModel.SessionId.Value,
            TimeStampUnixUtcMs = curretTime.ToUnixTimeMilliseconds()
        };
        
        return new IntroduceRequest
        {
            ChatMessage = new IntroduceRequest.Types.ChatMessage
            {
                Signed = signed,
                SignedSignature = ByteString.CopyFrom(_selfEncryptionService.Ephemeral.SignData(
                    signed.ToByteArray(),
                    HashAlgorithmName.SHA256,
                    RSASignaturePadding.Pkcs1
                )),
            }
        }.ToByteArray();
    }

    public void AddKnownAnnouncers(IEnumerable<AnnouncerModel> announcerModels)
    {
        foreach (var model in announcerModels)
        {
            if (_announcersByIdentity.TryAdd(model.Identity, model))
            {
                _announcerAdded.OnNext(model.Identity);
            }
        }
    }
}

internal class DummyDisposable : IDisposable
{
    public void Dispose()
    {
        
    }
}
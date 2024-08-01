using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using Google.Protobuf;
using Microsoft.Extensions.Logging;
using Percolator.Desktop.Crypto;
using Percolator.Desktop.Udp;
using Percolator.Desktop.Udp.Interfaces;
using Percolator.Protobuf.Announce;
using Percolator.Protobuf.Introduce;
using R3;

namespace Percolator.Desktop.Main;

public class MainService : IAnnouncerService
{
    private readonly UdpClientFactory _udpClientFactory;
    private readonly ILogger<MainService> _logger;
    private readonly SelfEncryptionService _selfEncryptionService;
    private readonly ILoggerFactory _loggerFactory;
    private readonly IBroadcaster _broadcaster;
    private readonly IListener _broadcastListener;
    private CancellationTokenSource _broadcastListenCts=new();
    private readonly Observable<Unit> _announceInterval;
    private IDisposable _announceSubscription = new DummyDisposable();
    private readonly ConcurrentDictionary<ByteString, AnnouncerModel> _announcersByIdentity= new();
    public ReactiveProperty<string> PreferredNickname { get; }
    
    public Observable<ByteString> AnnouncerAdded => _announcerAdded;
    private Subject<ByteString> _announcerAdded = new();
    private CancellationTokenSource _introduceListenCts = new();
    private IListener _introduceListener;
    private readonly ConcurrentBag<IPAddress> _ipBlacklist = new();
    private readonly ConcurrentBag<ByteString> _identityBlacklist = new();

    public MainService(
        UdpClientFactory udpClientFactory,
        ILogger<MainService> logger,
        SelfEncryptionService selfEncryptionService,
        ILoggerFactory loggerFactory)
    {
        _udpClientFactory = udpClientFactory;
        _logger = logger;
        _selfEncryptionService = selfEncryptionService;
        _loggerFactory = loggerFactory;
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

        PreferredNickname = new ReactiveProperty<string>(GetRandomNickname());
        _introduceListener = _udpClientFactory.CreateListener(Defaults.DefaultIntroducePort);
        _introduceListener.Received 
            .ObserveOn(ingressContext)
            .Subscribe(OnReceivedIntroduce);
    }

    private string GetRandomNickname()
    {
        var random = new Random();
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

    public void Listen()
    {
        _broadcastListenCts.Cancel();
        _broadcastListenCts = new CancellationTokenSource();
        _broadcastListener.Listen(_broadcastListenCts.Token);
    }
    
    public void StopListen()
    {
        _broadcastListenCts.Cancel();
    }

    private void OnReceivedIntroduce(UdpReceiveResult context)
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

        var currentUtcTime = DateTimeOffset.UtcNow;
        //todo: make this configurable
        var timestampGracePeriod = TimeSpan.FromMinutes(1);
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

        var identity = new RSACryptoServiceProvider()
        {
            PersistKeyInCsp = false
        };
        identity.ImportRSAPublicKey(payload.IdentityKey.ToByteArray(),out _);

        if (!identity.VerifyData(payload.ToByteArray(), payload.IdentityKey.ToByteArray(), HashAlgorithmName.SHA256,
                RSASignaturePadding.Pkcs1))
        {
            _logger.LogWarning("payload signature is invalid");
            return;
        }

        var didAdd = false;
        var announcer =_announcersByIdentity.GetOrAdd(payload.IdentityKey,_ =>
        {
            didAdd = true; 
            return new AnnouncerModel(payload.IdentityKey, _loggerFactory.CreateLogger<AnnouncerModel>());
        });
        announcer.AddIpAddress(context.RemoteEndPoint.Address);
        announcer.LastSeen.Value = DateTimeOffset.Now;
        var ephemeral = new RSACryptoServiceProvider
        {
            PersistKeyInCsp = false
        };
        ephemeral.ImportRSAPublicKey(payload.EphemeralKey.ToByteArray(),out _);
        announcer.Ephemeral.Value = ephemeral;

        if (didAdd)
        {
            _announcerAdded.OnNext(announcer.Identity);
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
                if (identityMessage == null || identityMessage.Payload == null)
                {
                    _logger.LogWarning("Announce message does not have valid payload");
                    return;
                }

                if (!identityMessage.Payload.HasIdentityKey ||
                    identityMessage.Payload.IdentityKey.Length == 0 ||
                    identityMessage.Payload.IdentityKey.Length > arbitraryMaxLength)
                {
                    _logger.LogWarning("Announce message does not have valid identity key");
                    return;
                }
                
                if (_identityBlacklist.Contains(identityMessage.Payload.IdentityKey))
                {
                    _logger.LogWarning("Blacklisted identity received from {Ip}", context.RemoteEndPoint.Address);
                    return;
                }
                
                if (identityMessage.Payload.IdentityKey.Equals(
                        ByteString.CopyFrom(_selfEncryptionService.Identity.ExportRSAPublicKey())))
                {
                    return;
                }

                //todo: make this configurable
                var timestampGracePeriod = TimeSpan.FromMinutes(1);
                
                if (!identityMessage.Payload.HasTimeStampUnixUtcMs ||
                    identityMessage.Payload.TimeStampUnixUtcMs > currentUtcTime.Add(timestampGracePeriod).ToUnixTimeMilliseconds() ||
                    identityMessage.Payload.TimeStampUnixUtcMs < currentUtcTime.Add(-timestampGracePeriod).ToUnixTimeMilliseconds())
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

                if (identityMessage.Payload.HasPort &&
                    identityMessage.Payload.Port <= 0)
                {
                    _logger.LogWarning("Announce message does not have a valid port");
                    return;
                }

                if (identityMessage.Payload.HasPreferredNickname &&
                    identityMessage.Payload.PreferredNickname.Length > arbitraryMaxLength)
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
            announcerModel.Nickname.Value = nextNick;
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
                await _broadcaster.Broadcast(GetAnnounceIdentityBytes(DateTimeOffset.Now),c);
            });
    }

    private byte[] GetAnnounceIdentityBytes(DateTimeOffset currentTime,int? handshakePort=null)
    {
        var payload = new AnnounceMessage.Types.Identity.Types.Payload()
        {
            IdentityKey = ByteString.CopyFrom(_selfEncryptionService.Identity.ExportRSAPublicKey()),
            TimeStampUnixUtcMs = currentTime.ToUniversalTime().ToUnixTimeMilliseconds(),
            PreferredNickname = PreferredNickname.Value
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

    public void StopAnnounce()
    {
        _announceSubscription.Dispose();
    }

    public IReadOnlyDictionary<ByteString, AnnouncerModel> Announcers => _announcersByIdentity;

    public void BeginIntroduceListen()
    {
        _introduceListenCts.Cancel();
        _introduceListenCts = new CancellationTokenSource();
        _introduceListener.Listen(_broadcastListenCts.Token);
    }

    public void StopIntroduceListen()
    {
        _introduceListenCts.Cancel();
    }

    public async Task SendIntroduction(IPAddress destination, int port, CancellationToken cancellationToken = default)
    {
        var udpClient = _udpClientFactory.GetOrCreateSender(port);
        await udpClient.Send(destination, GetUnknownPublicKeyBytes(DateTimeOffset.Now), cancellationToken);
    }

    private byte[] GetUnknownPublicKeyBytes(DateTimeOffset currentTime)
    {
        var payload = new IntroduceRequest.Types.Payload
        {
            IdentityKey =ByteString.CopyFrom( _selfEncryptionService.Identity.ExportRSAPublicKey()),
            EphemeralKey = ByteString.CopyFrom(_selfEncryptionService.Ephemeral.ExportRSAPublicKey()),
            TimeStampUnixUtcMs = currentTime.ToUniversalTime().ToUnixTimeMilliseconds()
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
}

internal class DummyDisposable : IDisposable
{
    public void Dispose()
    {
        
    }
}
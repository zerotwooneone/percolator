using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Security.Cryptography;
using Google.Protobuf;
using Microsoft.Extensions.Logging;
using Percolator.Desktop.Crypto;
using Percolator.Desktop.Udp;
using Percolator.Desktop.Udp.Interfaces;
using Percolator.Protobuf.Announce;
using R3;

namespace Percolator.Desktop.Main;

public class MainService
{
    private readonly UdpClientFactory _udpClientFactory;
    private readonly ILogger<MainService> _logger;
    private readonly SelfEncryptionService _selfEncryptionService;
    private readonly IBroadcaster _broadcaster;
    private readonly IListener _listener;
    private CancellationTokenSource _ListenCts=new();
    private readonly Observable<Unit> _announceInterval;
    private IDisposable _announceSubscription = new DummyDisposable();
    private byte[] _announceBytes;
    private readonly ConcurrentDictionary<ByteString, AnnouncerModel> _announcersByIdentity= new();
    public ReactiveProperty<string> PreferredNickname { get; }

    public MainService(
        UdpClientFactory udpClientFactory,
        ILogger<MainService> logger,
        SelfEncryptionService selfEncryptionService)
    {
        _udpClientFactory = udpClientFactory;
        _logger = logger;
        _selfEncryptionService = selfEncryptionService;
        _broadcaster = _udpClientFactory.CreateBroadcaster(Defaults.DefaultBroadcastPort);
        _listener = _udpClientFactory.CreateListener(Defaults.DefaultBroadcastPort);
        _listener.Received
            .ObserveOn(new SynchronizationContext())
            .Subscribe(OnReceived);
        
        _announceInterval = Observable
            .Interval(TimeSpan.FromSeconds(5))  //todo: make configurable()
            .ObserveOn(new SynchronizationContext())
            .Publish()
            .RefCount();

        _selfEncryptionService.EphemeralChanged+=OnEphemeralChanged;
        PreferredNickname = new ReactiveProperty<string>(Environment.MachineName);
        _announceBytes = GetAnnounceIdentityBytes();
    }

    private void OnEphemeralChanged(object? sender, EventArgs e)
    {
        _announceBytes = GetAnnounceIdentityBytes();
    }

    public void Listen()
    {
        _ListenCts.Cancel();
        _ListenCts = new CancellationTokenSource();
        _listener.Listen(_ListenCts.Token);
    }
    
    public void StopListen()
    {
        _ListenCts.Cancel();
    }
    
    private void OnReceived(UdpReceiveResult context)
    {
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
            int? port = identityMessage.Payload.HasPort
                ? identityMessage.Payload.Port
                : null;
            return new AnnouncerModel(identityMessage.Payload.IdentityKey, port);
        });
        announcerModel.AddIpAddress(context.RemoteEndPoint.Address);
        announcerModel.LastSeen.Value= DateTimeOffset.Now;
        if (identityMessage.Payload.HasPort)
        {
            announcerModel.Port.Value = identityMessage.Payload.Port;
        }

        if (didAdd)
        {
            _announcerAdded.OnNext(identityMessage.Payload.IdentityKey);
        }
    }

    public Observable<ByteString> AnnouncerAdded => _announcerAdded;
    private Subject<ByteString> _announcerAdded = new();

    public void Announce()
    {
        _announceSubscription.Dispose();
        _announceSubscription = _announceInterval
            .Subscribe(_ =>
            {
                _broadcaster.Broadcast(_announceBytes);
            });
    }

    private byte[] GetAnnounceIdentityBytes(int? handshakePort=null)
    {
        var payload = new AnnounceMessage.Types.Identity.Types.Payload()
        {
            IdentityKey = ByteString.CopyFrom(_selfEncryptionService.Identity.ExportRSAPublicKey()),
            TimeStampUnixUtcMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            PreferredNickname = PreferredNickname.Value
        };
        if (handshakePort != null && handshakePort != Defaults.DefaultHandshakePort)
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
}

internal class DummyDisposable : IDisposable
{
    public void Dispose()
    {
        
    }
}
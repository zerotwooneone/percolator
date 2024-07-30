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
    public const int BroadcastPort = 12345;
    private ConcurrentDictionary<ByteString, ByteString> EpemeralByIdentity = new();
    private CancellationTokenSource _ListenCts=new();
    private readonly Observable<Unit> _announceInterval;
    private IDisposable _announceSubscription = new DummyDisposable();
    private byte[] _announceBytes;

    public MainService(
        UdpClientFactory udpClientFactory,
        ILogger<MainService> logger,
        SelfEncryptionService selfEncryptionService)
    {
        _udpClientFactory = udpClientFactory;
        _logger = logger;
        _selfEncryptionService = selfEncryptionService;
        _broadcaster = _udpClientFactory.CreateBroadcaster(BroadcastPort);
        _listener = _udpClientFactory.CreateListener(BroadcastPort);
        _listener.Received
            .ObserveOn(new SynchronizationContext())
            .Subscribe(OnReceived);
        
        _announceInterval = Observable
            .Interval(TimeSpan.FromSeconds(5))  //todo: make configurable()
            .ObserveOn(new SynchronizationContext())
            .Publish()
            .RefCount();

        _selfEncryptionService.EphemeralChanged+=OnEphemeralChanged;
        _announceBytes = GetAnnounceBytes();
        
    }

    private void OnEphemeralChanged(object? sender, EventArgs e)
    {
        _announceBytes = GetAnnounceBytes();
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
    
    private void OnReceived(UdpReceiveResult result)
    {
        if(result.Buffer == null || result.Buffer.Length == 0)
        {
            _logger.LogWarning("Empty buffer received from {Ip}", result.RemoteEndPoint.Address);
            return;
        }
        var announce = AnnounceMessage.Parser.ParseFrom(result.Buffer);
        if (announce.Payload == null || 
            announce.Payload.IdentityKey == null || 
            announce.Payload.IdentityKey.Length <=0 ||
            announce.Payload.TimeStampUnixUtcMs <=0 || 
            announce.PayloadSignature == null)
        {
            _logger.LogWarning("Announce message does not have valid payload");
            return;
        }

        if (announce.Payload.IdentityKey.Equals(
                ByteString.CopyFrom(_selfEncryptionService.Identity.ExportRSAPublicKey())))
        {
            return;
        }
        if(announce.EphemeralKey == null || 
           announce.EphemeralKey.Length <=0 ||
           announce.EphemeralKeySignature == null ||
           announce.EphemeralKeySignature.Length <=0)
        {
            _logger.LogWarning("Announce message does not have valid ephemeral key");
            return;
        }

        var identity = new RSACryptoServiceProvider
        {
            PersistKeyInCsp = false
        };
        identity.ImportRSAPublicKey(announce.Payload.IdentityKey.ToByteArray(), out _);
        if (!identity.VerifyData(announce.EphemeralKey.ToByteArray(), announce.EphemeralKey.ToByteArray(),
                HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1))
        {
            _logger.LogWarning("Announce message does not have valid ephemeral key signature");
            return;
        }

        var ephemeral = new RSACryptoServiceProvider
        {
            PersistKeyInCsp = false
        };
        ephemeral.ImportRSAPublicKey(announce.EphemeralKey.ToByteArray(), out _);

        var alreadyExists = false;
        EpemeralByIdentity.AddOrUpdate(announce.Payload.IdentityKey,
            _ => announce.EphemeralKey,
            (_, oldEphemeral) =>
            {
                if (oldEphemeral.Equals(announce.EphemeralKey))
                {
                    alreadyExists = true;
                    return oldEphemeral;
                }

                _logger.LogInformation("Ephemeral key changed for {Identity}", announce.Payload.IdentityKey.ToBase64());
                return announce.EphemeralKey;
            });
        if (alreadyExists)
        {
            return;
        }
        if(!ephemeral.VerifyData(announce.Payload.ToByteArray(), announce.PayloadSignature.ToByteArray(), 
            HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1))
        {
            _logger.LogWarning("Announce message does not have valid payload signature");
            return;
        }
        
    }

    public void Announce()
    {
        _announceSubscription.Dispose();
        _announceSubscription = _announceInterval
            .Subscribe(_ =>
            {
                _broadcaster.Broadcast(_announceBytes);
            });
    }

    private byte[] GetAnnounceBytes()
    {
        var payload = new AnnounceMessage.Types.Payload
        {
            IdentityKey = ByteString.CopyFrom(_selfEncryptionService.Identity.ExportRSAPublicKey()),
            TimeStampUnixUtcMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        };
        var payloadBytes = payload.ToByteArray();
        var announceBytes = new AnnounceMessage
        {
            EphemeralKey = ByteString.CopyFrom(_selfEncryptionService.Ephemeral.ExportRSAPublicKey()),
            EphemeralKeySignature = ByteString.CopyFrom(_selfEncryptionService.Identity.SignData(
                _selfEncryptionService.Ephemeral.ExportRSAPublicKey(), 
                HashAlgorithmName.SHA256, 
                RSASignaturePadding.Pkcs1)),
            Payload =payload,
            PayloadSignature = ByteString.CopyFrom(_selfEncryptionService.Ephemeral.SignData(
                payloadBytes, 
                HashAlgorithmName.SHA256, 
                RSASignaturePadding.Pkcs1
            ))
        }.ToByteArray();
        return announceBytes;
    }

    public void StopAnnounce()
    {
        _announceSubscription.Dispose();
    }
}

internal class DummyDisposable : IDisposable
{
    public void Dispose()
    {
        
    }
}
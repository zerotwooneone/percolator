using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Security.Cryptography;
using Google.Protobuf;
using Microsoft.Extensions.Logging;
using Percolator.Desktop.Udp;
using Percolator.Desktop.Udp.Interfaces;
using Percolator.Protobuf.Announce;
using R3;

namespace Percolator.Desktop.Main;

public class MainService
{
    private readonly UdpClientFactory _udpClientFactory;
    private readonly ILogger<MainService> _logger;
    private readonly IBroadcaster _broadcaster;
    private readonly IListener _listener;
    public const int BroadcastPort = 12345;
    private ConcurrentDictionary<ByteString, ByteString> EpemeralByIdentity = new();

    public MainService(
        UdpClientFactory udpClientFactory,
        ILogger<MainService> logger)
    {
        _udpClientFactory = udpClientFactory;
        _logger = logger;
        _broadcaster = _udpClientFactory.CreateBroadcaster(BroadcastPort);
        _listener = _udpClientFactory.CreateListener(BroadcastPort);
        _listener.Received
            .ObserveOn(new SynchronizationContext())
            .Subscribe(OnReceived);
    }
    public Task Listen(CancellationToken cancellationToken)
    {
        _listener.Listen(cancellationToken);
        
        return Task.CompletedTask;
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
     
}
using System.Security.Cryptography;
using Google.Protobuf;
using Microsoft.Extensions.Logging;
using Percolator.Application.Identity;
using Percolator.Application.KeyExchange;
using Percolator.Application.Sessions;
using Percolator.Contracts;
using Percolator.Cryptography;

namespace Percolator.Application.Network.Handshake;

internal sealed class InitiatorHelloService : IInitiatorHelloService
{
    private readonly ILogger<InitiatorHelloService> _logger;
    private readonly ActiveIdentityContext _active;
    private readonly IX3DHOrchestrator _x3dh;
    private readonly IDirectSessionManager _sessions;
    private readonly IMessageService _messageService;

    public InitiatorHelloService(
        ILogger<InitiatorHelloService> logger,
        ActiveIdentityContext active,
        IX3DHOrchestrator x3dh,
        IDirectSessionManager sessions,
        IMessageService messageService)
    {
        _logger = logger;
        _active = active;
        _x3dh = x3dh;
        _sessions = sessions;
        _messageService = messageService;
    }

    public async Task SendInitiatorHelloViaHostAsync(
        byte[] recipientPublicKeyHash,
        byte[] remoteIdentityKeySpki,
        Guid signedPreKeyId,
        Guid? oneTimePreKeyId,
        byte[] remotePreKeySpki,
        Percolator.Identity.PeerId hostPeerId,
        byte[]? initiatorPayload,
        CancellationToken cancellationToken)
    {
        if (_active.Keys is null)
            throw new InvalidOperationException("Active identity keys not loaded.");

        // Build remote bundle used for X3DH initiation
        var remoteId = new RatchetIdentityKey(remoteIdentityKeySpki);

        // Generate ephemeral and initiate X3DH
        using var eph = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var ephSpki = eph.PublicKey.ExportSubjectPublicKeyInfo();
        var remotePreKey = new RatchetEphemeralKey(remotePreKeySpki);
        var bundle = new X3dPreKeyBundle(remoteId, remotePreKey, OneTimePreKey: null);
        var shared = _x3dh.InitiateHandshake(bundle, eph);

        // Optional encrypted payload carrying the first opaque message
        var initialPlaintext = initiatorPayload is { Length: > 0 } ? new Plaintext(initiatorPayload) : null;
        var ratchetMessage = await _sessions.EstablishSessionAsInitiatorAsync(
            recipientPublicKeyHash,
            signedPreKeyId,
            oneTimePreKeyId,
            remoteId,
            remotePreKey,
            shared,
            eph,
            initialPlaintext,
            cancellationToken).ConfigureAwait(false);

        // Compose initiator hello
        var hello = new HandshakeInitiatorHello
        {
            Version = 1,
            InitiatorIdentityKeySpki = ByteString.CopyFrom(_active.Keys.IdentitySigningKey.ExportSubjectPublicKeyInfo()),
            InitiatorEphemeralKeySpki = ByteString.CopyFrom(ephSpki),
            SignedPreKeyId = ByteString.CopyFrom(signedPreKeyId.ToByteArray()),
        };
        if (oneTimePreKeyId.HasValue)
        {
            hello.OneTimePreKeyId = ByteString.CopyFrom(oneTimePreKeyId.Value.ToByteArray());
        }
        if (ratchetMessage is not null)
        {
            hello.EncryptedPayload = ByteString.CopyFrom(ratchetMessage.Value);
        }

        // Wrap as MQ enqueue request to Host
        var mqReq = new EnqueueOpaqueMessageRequest
        {
            Version = 1,
            RecipientPublicKeyHash = ByteString.CopyFrom(recipientPublicKeyHash),
            MessageBlob = ByteString.CopyFrom(hello.ToByteArray())
        };
        var toHost = new InternalEnvelope
        {
            MessageQueueEnvelope = new MessageQueueEnvelope
            {
                Version = 1,
                EnqueueOpaqueMessageRequest = mqReq
            }
        };

        await _messageService.SendMessageAsync(toHost, hostPeerId, cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("Sent HandshakeInitiatorHello enqueue request to Host {HostPeer}", hostPeerId);
    }
}
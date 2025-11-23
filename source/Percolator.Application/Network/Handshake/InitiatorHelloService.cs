using System.Security.Cryptography;
using Google.Protobuf;
using Microsoft.Extensions.Logging;
using Percolator.Application.Identity;
using Percolator.Application.KeyExchange;
using Percolator.Application.Sessions;
using Percolator.Contracts;
using Percolator.Cryptography;
using PreKeyBundle = Percolator.Cryptography.PreKeyBundle;

namespace Percolator.Application.Network.Handshake;

internal sealed class InitiatorHelloService : IInitiatorHelloService
{
    private readonly ILogger<InitiatorHelloService> _logger;
    private readonly ActiveIdentityContext _active;
    private readonly IMessageService _messageService;
    private readonly IPreHandshakeSessionStore _preHandshake;
    private readonly IX3dhDeriver _x3dh;

    public InitiatorHelloService(
        ILogger<InitiatorHelloService> logger,
        ActiveIdentityContext active,
        IMessageService messageService,
        IPreHandshakeSessionStore preHandshake,
        IX3dhDeriver x3dh)
    {
        _logger = logger;
        _active = active;
        _messageService = messageService;
        _preHandshake = preHandshake;
        _x3dh = x3dh;
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

        // Derive IRK via X3DH/HKDF using crypto domain deriver
        var localIkPriv = new PrivatePreKey(_active.Keys.IdentitySigningKey.ExportECPrivateKey());
        var remoteSpk = new PreKey(remotePreKeySpki);
        OneTimeKey? remoteOtk = null; // Not supplied by caller here
        var result = _x3dh.DeriveInitiator(remoteId, remoteSpk, remoteOtk, localIkPriv);
        var shared = result.InitialRootKey;

        // Do not pre-establish a session here; responder hello will finalize and assign the session id

        // Persist minimal prehandshake state (IRK + remote identity SPKI), ordered by recency
        if (_active.Identity is null)
            throw new InvalidOperationException("Active identity not loaded.");
        var rec = new PreHandshakeRecord(
            Id: 0,
            SelfIdentityId: _active.Identity.SelfIdentityId,
            RecipientPublicKeyHash: recipientPublicKeyHash,
            LocalRequestId: Guid.NewGuid(),
            InitiatorEphemeralPrivateKey: result.InitiatorEphemeralPrivateKey.Value,
            InitialRootKey: result.InitialRootKey.Value,
            CreatedAtUtc: DateTimeOffset.UtcNow,
            ExpiresAtUtc: DateTimeOffset.UtcNow.AddHours(1),
            RemoteIdentityKeySpki: remoteIdentityKeySpki);
        await _preHandshake.SaveAsync(rec, cancellationToken).ConfigureAwait(false);

        // Compose initiator hello
        var hello = new HandshakeInitiatorHello
        {
            Version = 1,
            InitiatorIdentityKeySpki = ByteString.CopyFrom(_active.Keys.IdentitySigningKey.ExportSubjectPublicKeyInfo()),
            InitiatorEphemeralKeySpki = ByteString.CopyFrom(result.InitiatorEphemeralPublicKey.Value),
            SignedPreKeyId = ByteString.CopyFrom(signedPreKeyId.ToByteArray()),
        };
        if (oneTimePreKeyId.HasValue)
        {
            hello.OneTimePreKeyId = ByteString.CopyFrom(oneTimePreKeyId.Value.ToByteArray());
        }
        // No encrypted payload attached in the hello; responder will send first message

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
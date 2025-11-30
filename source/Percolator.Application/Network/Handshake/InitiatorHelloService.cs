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
    private readonly IHandshakePlanner _planner;
    private readonly ISessionCrypto _sessionCrypto;

    public InitiatorHelloService(
        ILogger<InitiatorHelloService> logger,
        ActiveIdentityContext active,
        IMessageService messageService,
        IPreHandshakeSessionStore preHandshake,
        IHandshakePlanner planner,
        ISessionCrypto sessionCrypto)
    {
        _logger = logger;
        _active = active;
        _messageService = messageService;
        _preHandshake = preHandshake;
        _planner = planner;
        _sessionCrypto = sessionCrypto;
    }

    public async Task SendInitiatorHelloViaHostAsync(
        byte[] recipientPublicKeyHash,
        PreKeyBundle remoteBundle,
        Guid signedPreKeyId,
        Guid? oneTimePreKeyId,
        Percolator.Identity.PeerId hostPeerId,
        byte[]? initiatorPayload,
        CancellationToken cancellationToken)
    {
        if (_active.Keys is null)
            throw new InvalidOperationException("Active identity keys not loaded.");

        // Validate remote bundle and plan
        _planner.ValidatePreKeyBundle(remoteBundle);
        var remoteId = remoteBundle.IdentitySigningKey;

        // Derive IRK via X3DH/HKDF using crypto domain deriver
        var localIkPriv = new PrivatePreKey(_active.Keys.IdentitySigningKey.ExportECPrivateKey());
        var x3 = _sessionCrypto.X3DH_Initiate(localIkPriv, remoteBundle);
        var shared = x3.SharedSecret;

        // Do not pre-establish a session here; responder hello will finalize and assign the session id

        // Persist minimal prehandshake state (IRK + remote identity SPKI), ordered by recency
        if (_active.Identity is null)
            throw new InvalidOperationException("Active identity not loaded.");
        var rec = new PreHandshakeRecord(
            Id: 0,
            SelfIdentityId: _active.Identity.SelfIdentityId.Value,
            RecipientPublicKeyHash: recipientPublicKeyHash,
            LocalRequestId: Guid.NewGuid(),
            InitiatorEphemeralPrivateKey: Array.Empty<byte>(),
            InitialRootKey: shared.Value,
            CreatedAtUtc: DateTimeOffset.UtcNow,
            ExpiresAtUtc: DateTimeOffset.UtcNow.AddHours(1),
            RemoteIdentityKeySpki: remoteId.Value);
        await _preHandshake.SaveAsync(rec, cancellationToken).ConfigureAwait(false);

        // Compose initiator hello
        var hello = new HandshakeInitiatorHello
        {
            Version = 1,
            InitiatorIdentityKeySpki = ByteString.CopyFrom(_active.Keys.IdentitySigningKey.ExportSubjectPublicKeyInfo()),
            InitiatorEphemeralKeySpki = ByteString.CopyFrom(x3.EphemeralPublic.Value),
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
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
    private readonly IMessageService _messageService;
    private readonly IPreHandshakeSessionStore _preHandshake;

    public InitiatorHelloService(
        ILogger<InitiatorHelloService> logger,
        ActiveIdentityContext active,
        IMessageService messageService,
        IPreHandshakeSessionStore preHandshake)
    {
        _logger = logger;
        _active = active;
        _messageService = messageService;
        _preHandshake = preHandshake;
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
        var shared = new SharedSecret(Array.Empty<byte>());
        throw new NotSupportedException("Initiator hello cutover pending (Step 8): replace legacy InitiateHandshake");

        // Do not pre-establish a session here; responder hello will finalize and assign the session id

        // Persist minimal prehandshake state (IRK + remote identity SPKI), ordered by recency
        if (_active.Identity is null)
            throw new InvalidOperationException("Active identity not loaded.");
        var rec = new PreHandshakeRecord(
            Id: 0,
            SelfIdentityId: _active.Identity.SelfIdentityId,
            RecipientPublicKeyHash: recipientPublicKeyHash,
            LocalRequestId: Guid.NewGuid(),
            InitiatorEphemeralPrivateKey: Array.Empty<byte>(),
            InitialRootKey: shared.Value,
            CreatedAtUtc: DateTimeOffset.UtcNow,
            ExpiresAtUtc: DateTimeOffset.UtcNow.AddHours(1),
            RemoteIdentityKeySpki: remoteIdentityKeySpki);
        await _preHandshake.SaveAsync(rec, cancellationToken).ConfigureAwait(false);

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
using Desktop.Wpf.Features.Sessions.Commands;
using Google.Protobuf;
using Grpc.Core;
using MediatR;
using Percolator.Application.Network.Handshake;
using Percolator.Application.Services;
using Percolator.Contracts;
using Percolator.Cryptography;
using Percolator.Identity;
using Percolator.Network;
using System.Net;
using System.Security.Cryptography;
using Percolator.Application.Identity;
using Percolator.Application.Network;
using Percolator.Network.Services;

namespace Desktop.Wpf.Features.Sessions.Handlers;

public sealed class ConnectViaNetworkCommandHandler : IRequestHandler<ConnectViaNetworkCommand, ConnectViaNetworkResult>
{
    private readonly IMainReverseSignalInviteFactory _reverseSignalInvites;
    private readonly ISessionEstablishmentTransport _sessionTransport;
    private readonly ISecureMessagingService _secureMessaging;
    private readonly IMessageTransportService _transport;
    private readonly IDirectSessionRepository _directSessions;
    private readonly IPeerIdentityRepository _peerIdentities;
    private readonly ISessionCrypto _sessionCrypto;
    private readonly IPreHandshakeSessionStore _preHandshake;
    private readonly ISentInvitationRepository _sentInvitations;
    private readonly IClock _clock;
    private readonly ActiveIdentityContext _active;
    private readonly IMediator _mediator;

    public ConnectViaNetworkCommandHandler(
        IMainReverseSignalInviteFactory reverseSignalInvites,
        ISessionEstablishmentTransport sessionTransport,
        ISecureMessagingService secureMessaging,
        IMessageTransportService transport,
        IDirectSessionRepository directSessions,
        IPeerIdentityRepository peerIdentities,
        ISessionCrypto sessionCrypto,
        IPreHandshakeSessionStore preHandshake,
        ISentInvitationRepository sentInvitations,
        IClock clock,
        ActiveIdentityContext active,
        IMediator mediator)
    {
        _reverseSignalInvites = reverseSignalInvites;
        _sessionTransport = sessionTransport;
        _secureMessaging = secureMessaging;
        _transport = transport;
        _directSessions = directSessions;
        _peerIdentities = peerIdentities;
        _sessionCrypto = sessionCrypto;
        _preHandshake = preHandshake;
        _sentInvitations = sentInvitations;
        _clock = clock;
        _active = active;
        _mediator = mediator;
    }

    public async Task<ConnectViaNetworkResult> Handle(ConnectViaNetworkCommand request, CancellationToken cancellationToken)
    {
        if (_active.Identity is null)
        {
            return new ConnectViaNetworkResult.Failed("Identity not loaded.");
        }

        if (request.RouteMode == "direct")
        {
            return await HandleDirectModeAsync(request, cancellationToken);
        }

        if (request.RouteMode == "relay")
        {
            return await HandleRelayModeAsync(request, cancellationToken);
        }

        return new ConnectViaNetworkResult.Failed("Unknown route mode.");
    }

    private async Task<ConnectViaNetworkResult> HandleDirectModeAsync(ConnectViaNetworkCommand request, CancellationToken cancellationToken)
    {
        try
        {
            var endpoint = ParseDnsEndPoint(request.DirectEndpoint);
            
            if (_active.Identity is null)
            {
                return new ConnectViaNetworkResult.Failed("Identity not loaded.");
            }

            var invite = _reverseSignalInvites.CreateInvite(
                targetDisplayName: request.TargetDisplayName,
                targetEndpointHost: endpoint.Host,
                targetEndpointPort: endpoint.Port,
                listeningPort: _active.Identity.ListeningPort);

            _ = await _sessionTransport.EstablishDirectSessionAsync(endpoint, invite);

            return new ConnectViaNetworkResult.Success();
        }
        catch (RpcException rpcEx) when (rpcEx.StatusCode == StatusCode.Unavailable)
        {
            return new ConnectViaNetworkResult.TargetOffline();
        }
        catch (Exception ex)
        {
            return new ConnectViaNetworkResult.Failed(ex.Message);
        }
    }

    private async Task<ConnectViaNetworkResult> HandleRelayModeAsync(ConnectViaNetworkCommand request, CancellationToken cancellationToken)
    {
        if (request.RelayHostPeerId is null)
        {
            return new ConnectViaNetworkResult.Failed("Select a relay host.");
        }

        byte[] targetIdentityPublicKeyHash;
        try
        {
            targetIdentityPublicKeyHash = ParsePkh32(request.TargetPkhText);
        }
        catch (Exception ex)
        {
            return new ConnectViaNetworkResult.Failed(ex.Message);
        }

        if (_active.Identity is null)
        {
            return new ConnectViaNetworkResult.Failed("Identity not loaded.");
        }

        var relayHostPeerId = new Percolator.Identity.PeerId(request.RelayHostPeerId.Value);
        var selfIdentityId = _active.Identity.SelfIdentityId.Value;

        DirectSession? direct;
        try
        {
            direct = await _directSessions
                .GetByRemotePeerIdAsync(new Percolator.Network.PeerId(relayHostPeerId.Value), selfIdentityId);
        }
        catch (Exception ex)
        {
            return new ConnectViaNetworkResult.Failed(ex.Message);
        }

        if (direct is null)
        {
            return new ConnectViaNetworkResult.Failed("No direct session to relay host.");
        }

        GetPreKeyBundleResponse.Types.PreKeyBundle? bundle;
        try
        {
            var internalEnvelope = new InternalEnvelope
            {
                PrekeyEnvelope = new PrekeyEnvelope
                {
                    Version = 1,
                    GetPreKeyBundleRequest = new GetPreKeyBundleRequest
                    {
                        Version = 1,
                        PublicKeyHash = ByteString.CopyFrom(targetIdentityPublicKeyHash)
                    }
                }
            };

            var plaintext = Plaintext.FromBytesOwned(internalEnvelope.ToByteArray());
            var cryptoSessionId = new SessionId(direct.SessionId.Value);
            var cipher = await _secureMessaging
                .EncryptAsync(cryptoSessionId, plaintext);

            var deliverResp = await _transport
                .SendMessageAsync(new Percolator.Identity.PeerId(relayHostPeerId.Value), direct.SessionId, cipher);
            var response = deliverResp.OriginalResponse;

            if (response.ResultCase != DeliverOpaqueMessageResponse.ResultOneofCase.ResponsePayload
                || response.ResponsePayload is null
                || !response.ResponsePayload.HasResponsePayload
                || response.ResponsePayload.ResponsePayload.Length == 0)
            {
                throw new InvalidOperationException("No response payload returned.");
            }

            var responseBytes = response.ResponsePayload.ResponsePayload.ToByteArray();
            var respCipher = SessionRatchetMessage.FromBytesOwned(responseBytes);
            var resolved = await _secureMessaging
                .DecryptInboundAsync(selfIdentityId, respCipher);
            var respPlain = resolved?.plaintext;
            if (respPlain is null)
            {
                throw new InvalidOperationException("Could not decrypt pre-key bundle response.");
            }

            var internalResp = InternalEnvelope.Parser.ParseFrom(respPlain.Span);
            if (internalResp.ApplicationPayloadCase != InternalEnvelope.ApplicationPayloadOneofCase.GetPreKeyBundleResponse)
            {
                throw new InvalidOperationException("Unexpected response type.");
            }

            bundle = internalResp.GetPreKeyBundleResponse?.PreKeyBundle;
        }
        catch (Exception ex)
        {
            return new ConnectViaNetworkResult.Failed(ex.Message);
        }

        if (bundle is null)
        {
            return new ConnectViaNetworkResult.Failed("Target not found.");
        }

        if (!bundle.HasIdentityKey || bundle.IdentityKey.Length == 0
            || !bundle.HasSignedPreKeyId || bundle.SignedPreKeyId.Length == 0
            || !bundle.HasSignedPreKey || bundle.SignedPreKey.Length == 0
            || !bundle.HasPreKeySignature || bundle.PreKeySignature.Length == 0)
        {
            return new ConnectViaNetworkResult.Failed("Pre-key bundle invalid.");
        }

        // Verify PKH matches returned identity key and verify signature.
        byte[] actualRemotePkh = SHA256.HashData(bundle.IdentityKey.Span);
        byte[] remoteIdentitySpki = bundle.IdentityKey.ToByteArray();
        if (!actualRemotePkh.AsSpan().SequenceEqual(targetIdentityPublicKeyHash))
        {
            return new ConnectViaNetworkResult.Failed("Remote identity key does not match requested PKH.");
        }

        Guid signedPreKeyId;
        try
        {
            signedPreKeyId = new Guid(bundle.SignedPreKeyId.Span);
        }
        catch
        {
            return new ConnectViaNetworkResult.Failed("Signed pre-key id invalid.");
        }

        Guid? oneTimePreKeyId = null;
        OneTimeKey? oneTimePreKey = null;
        if (bundle.OneTimeKeys.Count > 0)
        {
            var first = bundle.OneTimeKeys.FirstOrDefault(k => k is not null && k.OneTimeKeyId.Length > 0 && k.KeyBytes.Length > 0);
            if (first is not null)
            {
                try
                {
                    oneTimePreKeyId = new Guid(first.OneTimeKeyId.Span);
                    oneTimePreKey = OneTimeKey.FromSpan(first.KeyBytes.Span);
                }
                catch
                {
                    oneTimePreKeyId = null;
                    oneTimePreKey = null;
                }
            }
        }

        var remoteIdentity = RatchetIdentityKey.FromBytes(remoteIdentitySpki);
        var remoteSpk = PreKey.FromSpan(bundle.SignedPreKey.Span);
        var remoteSig = Percolator.Cryptography.Signature.FromSpan(bundle.PreKeySignature.Span);
        if (!_sessionCrypto.VerifySignature(remoteIdentity, remoteSpk, remoteSig))
        {
            return new ConnectViaNetworkResult.Failed("Pre-key bundle signature invalid.");
        }

        if (_active.Keys?.IdentitySigningKey is null)
        {
            return new ConnectViaNetworkResult.Failed("Identity keys not loaded.");
        }

        // Build X3DH initiator state (persist only what is needed for slow-path finalize: initial root key).
        var pkb = new Percolator.Cryptography.PreKeyBundle(
            remoteIdentity,
            signedPreKeyId,
            remoteSpk,
            remoteSig,
            oneTimePreKeyId,
            oneTimePreKey,
            expirationDateUtc: null);

        var localIkPriv = PrivatePreKey.FromBytesOwned(_active.Keys.IdentitySigningKey.ExportECPrivateKey());
        var x3 = _sessionCrypto.X3DH_Initiate(localIkPriv, pkb);

        var correlationId = Guid.NewGuid();
        var nowUtc = _clock.UtcNow;
        var expiresAtUtc = nowUtc.AddMinutes(10);

        try
        {
            await _preHandshake.SaveAsync(
                    new PreHandshakeRecord(
                        Id: 0,
                        SelfIdentityId: selfIdentityId,
                        RecipientPublicKeyHash: targetIdentityPublicKeyHash,
                        LocalRequestId: correlationId,
                        InitiatorEphemeralPrivateKey: Array.Empty<byte>(),
                        InitialRootKey: x3.SharedSecret.ToArray(),
                        CreatedAtUtc: nowUtc,
                        ExpiresAtUtc: expiresAtUtc,
                        RemoteIdentityKeySpki: remoteIdentitySpki),
                    CancellationToken.None);
        }
        catch (Exception ex)
        {
            return new ConnectViaNetworkResult.Failed(ex.Message);
        }

        // Persist route provenance for PendingOutbound row (relay host), keyed by correlation id.
        try
        {
            await _sentInvitations.UpsertAsync(
                    new SentInvitation(
                        new Percolator.Cryptography.Primitives.RequestCorrelationId(correlationId),
                        signedPreKeyId,
                        oneTimePreKeyId,
                        targetPeerId: null,
                        createdAtUtc: nowUtc,
                        expiresAtUtc: expiresAtUtc,
                        targetDisplayName: request.TargetDisplayName,
                        targetEndpointHost: null,
                        targetEndpointPort: null,
                        inviteRouteKind: InviteRouteKind.Relayed,
                        inviteRelayHostPeerId: new Percolator.Cryptography.Primitives.PeerId(relayHostPeerId.Value)))
                .ConfigureAwait(false);

            await _mediator.Publish(
                    new SentInvitationUpsertedNotification(new Percolator.Cryptography.Primitives.RequestCorrelationId(correlationId)),
                    CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return new ConnectViaNetworkResult.Failed(ex.Message);
        }

        // Create initiator hello and enqueue it to relay host for forwarding to target PKH.
        var hello = new HandshakeInitiatorHello
        {
            Version = 1,
            InitiatorIdentityKeySpki = ByteString.CopyFrom(_active.Keys.IdentitySigningKey.ExportSubjectPublicKeyInfo()),
            InitiatorEphemeralKeySpki = ByteString.CopyFrom(x3.EphemeralPublic.ToArray()),
            SignedPreKeyId = ByteString.CopyFrom(signedPreKeyId.ToByteArray())
        };
        if (oneTimePreKeyId is not null)
        {
            hello.OneTimePreKeyId = ByteString.CopyFrom(oneTimePreKeyId.Value.ToByteArray());
        }

        try
        {
            var mqReq = new EnqueueOpaqueMessageRequest
            {
                Version = 1,
                RecipientPublicKeyHash = ByteString.CopyFrom(targetIdentityPublicKeyHash),
                MessageBlob = ByteString.CopyFrom(hello.ToByteArray())
            };

            var env = new InternalEnvelope
            {
                MessageQueueEnvelope = new MessageQueueEnvelope
                {
                    Version = 1,
                    EnqueueOpaqueMessageRequest = mqReq
                }
            };

            var plainMq = Plaintext.FromBytesOwned(env.ToByteArray());
            var cryptoSessionIdMq = new SessionId(direct.SessionId.Value);
            var cipherMq = await _secureMessaging
                .EncryptAsync(cryptoSessionIdMq, plainMq)
                .ConfigureAwait(false);

            _ = await _transport
                .SendMessageAsync(new Percolator.Identity.PeerId(relayHostPeerId.Value), direct.SessionId, cipherMq, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return new ConnectViaNetworkResult.Failed(ex.Message);
        }

        return new ConnectViaNetworkResult.Success();
    }

    private static DnsEndPoint ParseDnsEndPoint(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new InvalidOperationException("Endpoint required.");
        }

        var trimmed = text.Trim();
        if (trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
            trimmed = trimmed.Substring("http://".Length);
        if (trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            trimmed = trimmed.Substring("https://".Length);

        var parts = trimmed.Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length != 2 || !int.TryParse(parts[1], out var port) || port <= 0)
        {
            throw new InvalidOperationException("Invalid endpoint format. Use host:port");
        }

        return new DnsEndPoint(parts[0], port);
    }

    private static byte[] ParsePkh32(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            throw new InvalidOperationException("Target PKH required.");

        var t = new string(text.Where(c => !char.IsWhiteSpace(c)).ToArray());
        if (t.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            t = t.Substring(2);

        if (t.Length % 2 != 0)
            throw new InvalidOperationException("PKH must be hex.");

        byte[] bytes;
        try
        {
            bytes = Convert.FromHexString(t);
        }
        catch
        {
            throw new InvalidOperationException("PKH must be hex.");
        }

        if (bytes.Length != 32)
            throw new InvalidOperationException("PKH must be 32 bytes (64 hex chars).");

        return bytes;
    }
}

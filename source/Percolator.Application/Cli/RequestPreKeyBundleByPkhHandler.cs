using Google.Protobuf;
using MediatR;
using Microsoft.Extensions.Logging;
using Percolator.Application.Identity;
using Percolator.Application.Network;
using Percolator.Application.Services;
using Percolator.Contracts;
using Percolator.Cryptography;
using Percolator.Identity;
using Percolator.Network;
using Percolator.Network.ValueObjects;
using System.Net;
using System.Security.Cryptography;

namespace Percolator.Application.Cli;

public class RequestPreKeyBundleByPkhHandler : IRequestHandler<RequestPreKeyBundleByPkhCommand, Unit>
{
    private readonly ILogger<RequestPreKeyBundleByPkhHandler> _logger;
    private readonly IDirectSessionLocator _directSessionLocator;
    private readonly ISecureMessagingService _secureMessaging;
    private readonly IMessageTransportService _transport;
    private readonly ActiveIdentityContext _activeIdentity;
    private readonly IPeerPublicSigningKeyStore _peerPublicSigningKeyStore;

    private readonly ISessionCrypto _sessionCrypto;
    private readonly IGrpcSessionService _grpcSessions;
    private readonly Percolator.Cryptography.ISessionRepository _sessions;
    private readonly IDirectSessionRepository _directSessions;
    private readonly IPeerRoutingProfileRepository _routingProfiles;
    private readonly IProfileRoutePlanner _routePlanner;
    private readonly IClock _clock;

    public RequestPreKeyBundleByPkhHandler(
        ILogger<RequestPreKeyBundleByPkhHandler> logger,
        IDirectSessionLocator directSessionLocator,
        ISecureMessagingService secureMessaging,
        IMessageTransportService transport,
        ActiveIdentityContext activeIdentity,
        IPeerPublicSigningKeyStore peerPublicSigningKeyStore,
        ISessionCrypto sessionCrypto,
        IGrpcSessionService grpcSessions,
        Percolator.Cryptography.ISessionRepository sessions,
        IDirectSessionRepository directSessions,
        IPeerRoutingProfileRepository routingProfiles,
        IProfileRoutePlanner routePlanner,
        IClock clock)
    {
        _logger = logger;
        _directSessionLocator = directSessionLocator;
        _secureMessaging = secureMessaging;
        _transport = transport;
        _activeIdentity = activeIdentity;
        _peerPublicSigningKeyStore = peerPublicSigningKeyStore;

        _sessionCrypto = sessionCrypto;
        _grpcSessions = grpcSessions;
        _sessions = sessions;
        _directSessions = directSessions;
        _routingProfiles = routingProfiles;
        _routePlanner = routePlanner;
        _clock = clock;
    }

    public async Task<Unit> Handle(RequestPreKeyBundleByPkhCommand request, CancellationToken cancellationToken)
    {
        if (request.PublicKeyHash is null || request.PublicKeyHash.Length == 0)
        {
            throw new ArgumentException("PublicKeyHash must be provided.", nameof(request.PublicKeyHash));
        }
        var identityPublicKeyHash = IdentityPublicKeyHash.FromBytes(request.PublicKeyHash);
        var hostPeerId = await _peerPublicSigningKeyStore
            .GetPeerIdByPublicKeyHashAsync(identityPublicKeyHash, cancellationToken)
            .ConfigureAwait(false);
        if (hostPeerId is null)
        {
            throw new InvalidOperationException("Peer not found by PKH.");
        }
        var hostPeer = new Peer(hostPeerId, hostPeerId.Value.ToString());

        if (_activeIdentity.Identity is null)
        {
            throw new InvalidOperationException("Active identity not loaded.");
        }
        var directHostSessionId = await _directSessionLocator.GetAsync(hostPeer.Id, _activeIdentity.Identity.SelfIdentityId.Value, cancellationToken).ConfigureAwait(false);
        if (directHostSessionId is null)
        {
            throw new InvalidOperationException("Direct session not found.");
        }

        var internalEnvelope = new InternalEnvelope
        {
            PrekeyEnvelope = new PrekeyEnvelope
            {
                Version = 1,
                GetPreKeyBundleRequest = new GetPreKeyBundleRequest
                {
                    Version = 1,
                    PublicKeyHash = ByteString.CopyFrom(request.PublicKeyHash)
                }
            }
        };

        // Encrypt and send
        var plaintext = new Plaintext(internalEnvelope.ToByteArray());
        var cryptoHostSessionId = new SessionId(directHostSessionId.Value.Value);
        var ratchetMessage = await _secureMessaging.EncryptAsync(cryptoHostSessionId, plaintext, cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("Requesting pre-key bundle from peer {PeerId}", hostPeer.Id);
        var deliverResp = await _transport.SendMessageAsync(hostPeer.Id, directHostSessionId.Value, ratchetMessage, cancellationToken).ConfigureAwait(false);

        if (deliverResp.ResultCase != DeliverOpaqueMessageResponse.ResultOneofCase.ResponsePayload
            || deliverResp.ResponsePayload is null
            || !deliverResp.ResponsePayload.HasResponsePayload)
        {
            throw new InvalidOperationException("No response payload returned.");
        }

        var respCipher = new SessionRatchetMessage(deliverResp.ResponsePayload.ResponsePayload.ToByteArray());
        var resolved = await _secureMessaging.DecryptInboundAsync(1, respCipher, cancellationToken).ConfigureAwait(false);
        var respPlain = resolved?.plaintext;
        if (respPlain is null)
        {
            throw new InvalidOperationException("Could not decrypt pre-key bundle response.");
        }

        var internalResp = InternalEnvelope.Parser.ParseFrom(respPlain.Value);
        if (internalResp.ApplicationPayloadCase != InternalEnvelope.ApplicationPayloadOneofCase.GetPreKeyBundleResponse)
        {
            _logger.LogWarning("Unexpected response type: {Type}", internalResp.ApplicationPayloadCase);
            throw new InvalidOperationException("Unexpected response type.");
        }

        if (internalResp.GetPreKeyBundleResponse == null)
        {
            throw new InvalidOperationException("No pre-key bundle returned.");
        }
        
        var preKeyBundle = internalResp.GetPreKeyBundleResponse.PreKeyBundle;

        var endpoint = await SelectEndpointAsync(hostPeer.Id, cancellationToken).ConfigureAwait(false);

        await PerformHandshake(hostPeer.Id, request.PublicKeyHash, endpoint, preKeyBundle, cancellationToken)
            .ConfigureAwait(false);
        
        return Unit.Value;
    }

    private async Task<DnsEndPoint> SelectEndpointAsync(Percolator.Identity.PeerId remotePeerId, CancellationToken ct)
    {
        var profile = await _routingProfiles.GetByIdAsync(new Percolator.Network.PeerId(remotePeerId.Value), ct)
            .ConfigureAwait(false);
        if (profile is null)
        {
            throw new InvalidOperationException("No routing profile for peer.");
        }

        var selection = _routePlanner.SelectRoute(profile);
        if (selection.Relay is not null)
        {
            throw new InvalidOperationException("Relay-only route selected; cannot perform direct EstablishSession.");
        }

        return selection.Endpoint.EndPoint;
    }

    private async Task PerformHandshake(
        Percolator.Identity.PeerId remotePeerId,
        byte[] expectedRemotePkh,
        DnsEndPoint endpoint,
        GetPreKeyBundleResponse.Types.PreKeyBundle bundle,
        CancellationToken ct)
    {
        if (_activeIdentity.Identity is null || _activeIdentity.Keys?.IdentitySigningKey is null)
        {
            throw new InvalidOperationException("Active identity not loaded.");
        }

        if (bundle.IdentityKey is null || bundle.IdentityKey.Length == 0)
            throw new InvalidOperationException("Pre-key bundle missing identity key.");
        if (bundle.SignedPreKeyId is null || bundle.SignedPreKeyId.Length == 0)
            throw new InvalidOperationException("Pre-key bundle missing signed pre-key id.");
        if (bundle.SignedPreKey is null || bundle.SignedPreKey.Length == 0)
            throw new InvalidOperationException("Pre-key bundle missing signed pre-key.");
        if (bundle.PreKeySignature is null || bundle.PreKeySignature.Length == 0)
            throw new InvalidOperationException("Pre-key bundle missing signature.");

        var remoteIdentitySpki = bundle.IdentityKey.ToByteArray();
        var remotePkh = SHA256.HashData(remoteIdentitySpki);
        if (!remotePkh.AsSpan().SequenceEqual(expectedRemotePkh))
        {
            throw new InvalidOperationException("Remote identity key does not match requested PKH.");
        }

        Guid signedPreKeyId;
        try
        {
            signedPreKeyId = new Guid(bundle.SignedPreKeyId.ToByteArray());
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("Signed pre-key id invalid.", ex);
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
                    oneTimePreKeyId = new Guid(first.OneTimeKeyId.ToByteArray());
                    oneTimePreKey = new OneTimeKey(first.KeyBytes.ToByteArray());
                }
                catch
                {
                    oneTimePreKeyId = null;
                    oneTimePreKey = null;
                }
            }
        }

        var remoteIdentity = new RatchetIdentityKey(remoteIdentitySpki);
        var remoteSpk = new PreKey(bundle.SignedPreKey.ToByteArray());
        var remoteSig = new Percolator.Cryptography.Signature(bundle.PreKeySignature.ToByteArray());

        if (!_sessionCrypto.VerifySignature(remoteIdentity, remoteSpk, remoteSig))
        {
            throw new InvalidOperationException("Pre-key bundle signature invalid.");
        }

        var pkb = new Percolator.Cryptography.PreKeyBundle(
            remoteIdentity,
            signedPreKeyId,
            remoteSpk,
            remoteSig,
            oneTimePreKeyId,
            oneTimePreKey,
            expirationDateUtc: null);

        var localIkPriv = new PrivatePreKey(_activeIdentity.Keys.IdentitySigningKey.ExportECPrivateKey());
        var x3 = _sessionCrypto.X3DH_Initiate(localIkPriv, pkb);

        var req = new EstablishSessionRequest
        {
            Version = 1,
            IdentitySigningKey = ByteString.CopyFrom(_activeIdentity.Keys.IdentitySigningKey.ExportSubjectPublicKeyInfo()),
            EphemeralKey = ByteString.CopyFrom(x3.EphemeralPublic.Value),
            PrekeyId = ByteString.CopyFrom(signedPreKeyId.ToByteArray())
        };
        if (oneTimePreKeyId is not null)
        {
            req.OnetimePrekeyId = ByteString.CopyFrom(oneTimePreKeyId.Value.ToByteArray());
        }

        var resp = await _grpcSessions.EstablishSessionAsync(endpoint, req, ct).ConfigureAwait(false);
        if (resp.Response is null || !resp.Response.HasResponsePayload || resp.Response.ResponsePayload.Length == 0)
        {
            throw new InvalidOperationException("Handshake failed.");
        }

        EstablishSessionResponse.Types.Response.Types.ResponsePayload respPayload;
        try
        {
            respPayload = EstablishSessionResponse.Types.Response.Types.ResponsePayload.Parser.ParseFrom(resp.Response.ResponsePayload);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("Handshake response invalid.", ex);
        }

        if (!respPayload.HasSessionId || string.IsNullOrWhiteSpace(respPayload.SessionId))
            throw new InvalidOperationException("Handshake response missing session id.");

        var sessionId = new SessionId(Guid.Parse(respPayload.SessionId));

        var root = new RootKey(x3.SharedSecret.Value);
        var initiatorSession = RatchetBootstrap.CreateInitiatorSession(
            sessionId,
            new Percolator.Cryptography.Primitives.PeerId(remotePeerId.Value),
            new ProtocolVersion(1),
            root,
            _clock,
            crypto: _sessionCrypto);

        await _sessions.AddAsync(initiatorSession, ct).ConfigureAwait(false);

        await _directSessions.UpsertAsync(
                new Percolator.Network.PeerId(remotePeerId.Value),
                new Percolator.Network.DirectSessionId(sessionId.Value),
                _activeIdentity.Identity.SelfIdentityId.Value)
            .ConfigureAwait(false);

        var profile = await _routingProfiles.GetByIdAsync(new Percolator.Network.PeerId(remotePeerId.Value), ct).ConfigureAwait(false)
            ?? new PeerRoutingProfile();
        if (profile.Id is null)
        {
            profile.BindIdentity(new Percolator.Network.PeerId(remotePeerId.Value));
        }
        profile.AddGrpcEndPoint(new GrpcEndPoint(endpoint, _clock.UtcNow), _clock.UtcNow);
        profile.SetIdentityPublicKey(new IdentityPublicKey(remoteIdentitySpki));
        await _routingProfiles.UpsertAsync(profile, ct).ConfigureAwait(false);
    }
}

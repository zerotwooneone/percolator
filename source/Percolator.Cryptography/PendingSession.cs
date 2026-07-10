using Percolator.Cryptography.Primitives;

namespace Percolator.Cryptography;

public class PendingSession
{
    public PendingSessionId Id { get; }
    public CryptoPeerId RemoteCryptoPeerId { get; }
    public ProtocolVersion ProtocolVersion { get; }
    public HandshakeInvitation Invitation { get; }
    public RequestCorrelationId RequestCorrelationId { get; }
    public bool IsRelayed { get; }
    public CryptoPeerId? RelayHostPeerId { get; }
    public RatchetIdentityKey? InviterIdentityKey { get; }
    public CryptoPublicIdentityId? InviterPublicIdentityId { get; }
    public string? CallbackEndpointHost { get; }
    public int? CallbackEndpointPort { get; }
    public ApprovalState State { get; private set; }
    public DateTimeOffset CreatedAtUtc { get; }
    public DateTimeOffset? ExpiresAtUtc { get; private set; }

    private PendingSession(
        PendingSessionId id,
        CryptoPeerId remoteCryptoPeerId,
        ProtocolVersion protocolVersion,
        HandshakeInvitation invitation,
        RequestCorrelationId requestCorrelationId,
        bool isRelayed,
        CryptoPeerId? relayHostPeerId,
        RatchetIdentityKey? inviterIdentityKey,
        CryptoPublicIdentityId? inviterPublicIdentityId,
        string? callbackEndpointHost,
        int? callbackEndpointPort,
        DateTimeOffset createdAtUtc,
        DateTimeOffset? expiresAtUtc)
    {
        Id = id;
        RemoteCryptoPeerId = remoteCryptoPeerId;
        ProtocolVersion = protocolVersion;
        Invitation = invitation;
        RequestCorrelationId = requestCorrelationId;
        IsRelayed = isRelayed;
        RelayHostPeerId = relayHostPeerId;
        InviterIdentityKey = inviterIdentityKey;
        InviterPublicIdentityId = inviterPublicIdentityId;
        CallbackEndpointHost = callbackEndpointHost;
        CallbackEndpointPort = callbackEndpointPort;
        State = ApprovalState.AwaitingApproval;
        CreatedAtUtc = createdAtUtc;
        ExpiresAtUtc = expiresAtUtc;

        if (CallbackEndpointHost is null ^ CallbackEndpointPort is null)
        {
            throw new InvalidOperationException("Callback endpoint host/port must either both be present or both be absent.");
        }

        if (IsRelayed && CallbackEndpointHost is not null)
        {
            throw new InvalidOperationException("Relayed pending sessions cannot store a callback endpoint.");
        }

        if (IsRelayed && RelayHostPeerId is null)
        {
            throw new InvalidOperationException("Relayed pending sessions must store the relay host peer id.");
        }

        if (!IsRelayed && RelayHostPeerId is not null)
        {
            throw new InvalidOperationException("Direct pending sessions cannot store a relay host peer id.");
        }
    }

    public static PendingSession FromInvitationWithMetadata(
        PendingSessionId id,
        CryptoPeerId remoteCryptoPeerId,
        ProtocolVersion protocolVersion,
        HandshakeInvitation invitation,
        RequestCorrelationId requestCorrelationId,
        bool isRelayed,
        CryptoPeerId? relayHostPeerId,
        RatchetIdentityKey? inviterIdentityKey,
        CryptoPublicIdentityId? inviterPublicIdentityId,
        string? callbackEndpointHost,
        int? callbackEndpointPort,
        IClock clock,
        DateTimeOffset? expiresAtUtc = null)
    {
        if (invitation is null) throw new ArgumentNullException(nameof(invitation));
        if (clock is null) throw new ArgumentNullException(nameof(clock));
        return new PendingSession(
            id,
            remoteCryptoPeerId,
            protocolVersion,
            invitation,
            requestCorrelationId: requestCorrelationId,
            isRelayed: isRelayed,
            relayHostPeerId: relayHostPeerId,
            inviterIdentityKey: inviterIdentityKey,
            inviterPublicIdentityId: inviterPublicIdentityId,
            callbackEndpointHost: callbackEndpointHost,
            callbackEndpointPort: callbackEndpointPort,
            createdAtUtc: clock.UtcNow,
            expiresAtUtc: expiresAtUtc);
    }

    public HandshakeResponseMessage ApproveAndRespond(ICryptoPrimitives crypto, IKeyStore keyStore)
    {
        if (crypto is null) throw new ArgumentNullException(nameof(crypto));
        if (keyStore is null) throw new ArgumentNullException(nameof(keyStore));
        if (State == ApprovalState.Rejected || State == ApprovalState.Expired)
            throw new InvalidOperationException("Cannot approve a rejected or expired pending session.");
        var response = crypto.CreateHandshakeResponse(Invitation, keyStore);
        State = ApprovalState.Approved;
        return response;
    }

    public HandshakeResponseMessage AutoRespond(ICryptoPrimitives crypto, IKeyStore keyStore, ApprovalPolicy policy)
    {
        if (policy is null) throw new ArgumentNullException(nameof(policy));
        if (!policy.AllowAutoRespond)
            throw new InvalidOperationException("Auto-respond is not permitted by policy.");
        if (crypto is null) throw new ArgumentNullException(nameof(crypto));
        if (keyStore is null) throw new ArgumentNullException(nameof(keyStore));
        if (State == ApprovalState.Rejected || State == ApprovalState.Expired)
            throw new InvalidOperationException("Cannot auto-respond a rejected or expired pending session.");
        var response = crypto.CreateHandshakeResponse(Invitation, keyStore);
        State = ApprovalState.AutoResponded;
        return response;
    }

    public void Reject()
    {
        if (State == ApprovalState.Approved || State == ApprovalState.AutoResponded)
            throw new InvalidOperationException("Cannot reject an already approved session.");
        State = ApprovalState.Rejected;
    }

    public void Expire(DateTimeOffset expiresAtUtc)
    {
        ExpiresAtUtc = expiresAtUtc;
        State = ApprovalState.Expired;
    }

    public bool IsExpiredAt(DateTimeOffset clockUtcNow)
    {
        if (ExpiresAtUtc is null)
        {
            return false;
        }

        if (State == ApprovalState.Expired)
        {
            return true;
        }
        return clockUtcNow >= ExpiresAtUtc.Value;
    }
}

using System;
using Percolator.Cryptography.Primitives;

namespace Percolator.Cryptography;

public class PendingSession
{
    public PendingSessionId Id { get; }
    public PeerId RemotePeerId { get; }
    public ProtocolVersion ProtocolVersion { get; }
    public HandshakeInvitation Invitation { get; }
    public ApprovalState State { get; private set; }
    public DateTimeOffset CreatedAtUtc { get; }
    public DateTimeOffset? ExpiresAtUtc { get; }
    private readonly ApprovalPolicy _policy;

    private PendingSession(
        PendingSessionId id,
        PeerId remotePeerId,
        ProtocolVersion protocolVersion,
        HandshakeInvitation invitation,
        DateTimeOffset createdAtUtc,
        DateTimeOffset? expiresAtUtc,
        ApprovalPolicy? policy = null)
    {
        Id = id;
        RemotePeerId = remotePeerId;
        ProtocolVersion = protocolVersion;
        Invitation = invitation;
        State = ApprovalState.AwaitingApproval;
        CreatedAtUtc = createdAtUtc;
        ExpiresAtUtc = expiresAtUtc;
        _policy = policy ?? ApprovalPolicy.Default;
    }

    public static PendingSession FromInvitation(
        PendingSessionId id,
        PeerId remotePeerId,
        ProtocolVersion protocolVersion,
        HandshakeInvitation invitation,
        IClock clock,
        DateTimeOffset? expiresAtUtc = null)
    {
        if (invitation is null) throw new ArgumentNullException(nameof(invitation));
        if (clock is null) throw new ArgumentNullException(nameof(clock));
        return new PendingSession(id, remotePeerId, protocolVersion, invitation, clock.UtcNow, expiresAtUtc);
    }

    public static PendingSession FromInvitation(
        PendingSessionId id,
        PeerId remotePeerId,
        ProtocolVersion protocolVersion,
        HandshakeInvitation invitation,
        ApprovalPolicy policy,
        IClock clock,
        DateTimeOffset? expiresAtUtc = null)
    {
        if (invitation is null) throw new ArgumentNullException(nameof(invitation));
        if (policy is null) throw new ArgumentNullException(nameof(policy));
        if (clock is null) throw new ArgumentNullException(nameof(clock));
        return new PendingSession(id, remotePeerId, protocolVersion, invitation, clock.UtcNow, expiresAtUtc, policy);
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

    public HandshakeResponseMessage AutoRespond(ICryptoPrimitives crypto, IKeyStore keyStore)
    {
        if (!_policy.AllowAutoRespond)
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

    public void Expire(IClock clock)
    {
        if (clock is null) throw new ArgumentNullException(nameof(clock));
        if (ExpiresAtUtc.HasValue && clock.UtcNow >= ExpiresAtUtc.Value)
        {
            State = ApprovalState.Expired;
        }
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

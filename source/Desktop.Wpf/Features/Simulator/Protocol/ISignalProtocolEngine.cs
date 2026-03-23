using Percolator.Cryptography;
using Percolator.Cryptography.Primitives;

namespace Desktop.Wpf.Features.Simulator.Protocol;

public interface ISignalProtocolEngine
{
    PreKeyBundle CreateStandardPreKeyBundle(
        SimulatedPeerModel peer,
        DateTimeOffset? expiresUtc,
        bool includeOneTimeKeys,
        int oneTimeKeyCount);

    InitiateStandardHandshakeResult? TryInitiateStandardHandshake(
        SimulatedPeerModel initiator,
        PreKeyBundle responderBundle);

    SessionRatchetMessage Encrypt(
        SimulatedPeerModel sender,
        SessionId sessionId,
        Plaintext plaintext);

    Plaintext Decrypt(
        SimulatedPeerModel receiver,
        SessionId sessionId,
        SessionRatchetMessage message);

    AcceptReverseSignalInviteResult AcceptReverseSignalInvite(
        SimulatedPeerModel acceptor,
        ReverseSignalInviteDomain invite,
        Guid inviterPeerId);

    FinalizeInviteHandshakeResponseResult? TryFinalizeInviteHandshakeResponse(
        SimulatedPeerModel inviter,
        InviteHandshakeResponseDomain response,
        Guid acceptorPeerId,
        Guid requestCorrelationId);
}

public sealed record InitiateStandardHandshakeResult(
    SessionId SessionId,
    byte[] InitiatorIdentitySigningKeySpki,
    byte[] InitiatorEphemeralKeySpki,
    Guid SignedPreKeyId,
    Guid? OneTimePreKeyId,
    byte[] InitialRootKey);

public sealed record ReverseSignalInviteDomain(
    Guid RequestCorrelationId,
    byte[] InviterIdentitySigningKeySpki,
    byte[] InviterSignedPreKeySpki,
    byte[] InviterSignedPreKeySignature,
    byte[]? InviterOneTimePreKeySpki,
    DateTimeOffset? ExpiresUtc);

public sealed record AcceptReverseSignalInviteResult(
    SessionId SessionId,
    byte[] AcceptorIdentitySigningKeySpki,
    byte[] AcceptorX3DhEphemeralKeySpki,
    byte[] InitialRatchetMessage);

public sealed record InviteHandshakeResponseDomain(
    Guid RequestCorrelationId,
    byte[] AcceptorIdentitySigningKeySpki,
    byte[] AcceptorX3DhEphemeralKeySpki,
    byte[] InitialRatchetMessage);

public sealed record FinalizeInviteHandshakeResponseResult(
    SessionId SessionId);

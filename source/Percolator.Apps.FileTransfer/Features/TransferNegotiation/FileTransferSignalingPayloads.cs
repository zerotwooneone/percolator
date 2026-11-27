using Percolator.Apps.FileTransfer.Domain.TransferNegotiation;

namespace Percolator.Apps.FileTransfer.Features.TransferNegotiation;

/// <summary>
/// Broadcast over Group V2 SenderKey when a peer joins a group transfer swarm so all group peers
/// can pin its transfer certificate fingerprint and swarm directly even when the original author goes offline.
/// </summary>
public sealed record FileTransferPeerJoinPayload(
    TransferSessionId TransferSessionId,
    EphemeralSwarmPeerId EphemeralPeerId,
    CertSha256Fingerprint CertSha256);

public enum IceSignalKind : byte
{
    SdpOffer = 0x01,
    SdpAnswer = 0x02,
    IceCandidate = 0x03,
    PipeRendezvous = 0x04
}

/// <summary>
/// E2E signaling envelope (transmitted strictly inside Double Ratchet or Group V2 SenderKey)
/// for WebRTC SDP Offer/Answer, trickle ICE candidates, DTLS fingerprint pinning, and optional
/// zero-disk TransferPipe rendezvous tokens.
/// </summary>
public sealed record FileTransferIceSignalPayload(
    TransferSessionId TransferSessionId,
    IceSignalKind Kind,
    string SdpOrCandidate,
    string? CandidateMid,
    CertSha256Fingerprint? DtlsFingerprintSha256,
    PipeRendezvousToken? PipeToken = null,
    RelayPipeEndpointUri? PipeEndpointUri = null);

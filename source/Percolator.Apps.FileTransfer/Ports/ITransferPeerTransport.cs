using Percolator.Apps.FileTransfer.Domain.TransferNegotiation;
using Percolator.Apps.FileTransfer.Features.TransferNegotiation;
using Percolator.Domain.Delivery.ValueObjects;
using PublicIdentityId = Percolator.Domain.Identities.PublicIdentityId;

namespace Percolator.Apps.FileTransfer.Ports;

public enum TransferTransportKind : byte
{
    DirectWebRtcDtls = 0x01,
    RelayedTransferPipeTls13 = 0x02
}

public static class TransferTransportErrorCodes
{
    public const string DirectNatTraversalFailed = "DIRECT_NAT_TRAVERSAL_FAILED";
    public const string DtlsFingerprintMismatch = "DTLS_FINGERPRINT_MISMATCH";
    public const string ProhibitedSdpMedia = "PROHIBITED_SDP_MEDIA";
    public const string SessionPeerCapExceeded = "SESSION_PEER_CAP_EXCEEDED";
    public const string HandshakeTimeout = "TRANSPORT_HANDSHAKE_TIMEOUT";
    public const string InvalidHandshakeToken = "TRANSPORT_INVALID_HANDSHAKE_TOKEN";
    public const string TransferPipeUnavailable = "TRANSFER_PIPE_UNAVAILABLE";
}

public sealed class TransferTransportException : Exception
{
    public string ErrorCode { get; }

    public TransferTransportException(string errorCode, string message, Exception? innerException = null)
        : base($"[{errorCode}] {message}", innerException)
    {
        ErrorCode = string.IsNullOrWhiteSpace(errorCode)
            ? throw new ArgumentException("Error code cannot be empty.", nameof(errorCode))
            : errorCode;
    }
}

public sealed record IceSignalOutboundEvent(
    TransferSessionId SessionId,
    PublicIdentityId RemotePeerId,
    IceSignalKind Kind,
    string SdpOrCandidate,
    string? CandidateMid,
    CertSha256Fingerprint? LocalDtlsFingerprint);

public sealed record DirectPeerConnectionOptions(
    TransferSessionId SessionId,
    PublicIdentityId RemotePeerId,
    bool IsInitiator,
    StunEndpointDescriptor? ExchangeStunEndpoint = null,
    TimeSpan? HandshakeTimeout = null,
    int MaxPeersPerSession = 16,
    int SendBufferHighWatermarkBytes = 512 * 1024,
    int SendBufferLowWatermarkBytes = 128 * 1024);

public sealed record RelayedPipeConnectionOptions(
    TransferSessionId SessionId,
    PublicIdentityId LocalIdentityId,
    PublicIdentityId RemotePeerId,
    RelayEndpoint ExchangeEndpoint,
    PipeRendezvousToken RendezvousToken,
    StreamAuthToken StreamAuthToken,
    bool IsTlsServer,
    CertSha256Fingerprint ExpectedRemoteCertSha256,
    bool ExchangeSupportsTransferPipe = true,
    TimeSpan? HandshakeTimeout = null,
    int MaxPeersPerSession = 16);

public interface ITransferDataChannel : ITransferStream
{
    string Label { get; }
    bool IsOpen { get; }
    long BufferedAmount { get; }
    TransferTransportKind TransportKind { get; }
    CertSha256Fingerprint VerifiedRemoteCertSha256 { get; }
}

public interface ITransferPeerConnection : IAsyncDisposable
{
    TransferSessionId SessionId { get; }
    PublicIdentityId RemotePeerId { get; }
    TransferTransportKind TransportKind { get; }
    CertSha256Fingerprint? LocalDtlsFingerprint { get; }
    CertSha256Fingerprint? RemoteDtlsFingerprint { get; }

    event Action<IceSignalOutboundEvent>? SignalProduced;

    ValueTask<string> CreateOfferAsync(CancellationToken ct = default);

    ValueTask<string> AcceptOfferAndCreateAnswerAsync(
        string remoteSdpOffer,
        CertSha256Fingerprint? expectedRemoteDtlsFingerprint,
        CancellationToken ct = default);

    ValueTask ApplyRemoteAnswerAsync(
        string remoteSdpAnswer,
        CertSha256Fingerprint? expectedRemoteDtlsFingerprint,
        CancellationToken ct = default);

    ValueTask AddRemoteIceCandidateAsync(
        string candidate,
        string mid,
        CancellationToken ct = default);

    ValueTask<ITransferDataChannel> WaitForAuthenticatedChannelAsync(
        StreamAuthToken expectedAuthToken,
        PublicIdentityId localIdentityId,
        CancellationToken ct = default);
}

public interface ITransferPeerTransport : IAsyncDisposable
{
    int ActiveConnectionCount { get; }

    ValueTask<ITransferPeerConnection> CreateDirectConnectionAsync(
        DirectPeerConnectionOptions options,
        CancellationToken ct = default);

    ValueTask<ITransferDataChannel> OpenRelayedPipeChannelAsync(
        RelayedPipeConnectionOptions options,
        CancellationToken ct = default);

    ValueTask CloseSessionConnectionsAsync(
        TransferSessionId sessionId,
        CancellationToken ct = default);

    ValueTask CloseAllConnectionsAsync(CancellationToken ct = default);
}

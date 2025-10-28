namespace Percolator.Network;

/// <summary>
/// Represents the persistent network connection information for a peer.
/// </summary>
public class PeerConnection
{
    /// <summary>
    /// The stable identifier of the peer, linking to the Identity domain.
    /// </summary>
    public PeerId Id { get; init; }

    /// <summary>
    /// The public key required to initiate a direct message session with this peer.
    /// </summary>
    public DirectMessagePublicKey? IdentitySigningKey { get; private set; }

    /// <summary>
    /// A list of known gRPC endpoints for this peer.
    /// </summary>
    public IReadOnlyList<GrpcEndPoint> GrpcEndPoints => _grpcEndPoints;
    private readonly List<GrpcEndPoint> _grpcEndPoints = new();

    /// <summary>
    /// A list of trusted TLS certificates for this peer.
    /// Used for certificate pinning to prevent MITM attacks.
    /// </summary>
    public IReadOnlyList<TlsCertificate> TlsCertificates { get; init; }

    /// <summary>
    /// The last time any activity was seen from this peer.
    /// </summary>
    public DateTimeOffset LastSeen { get; private set; }

    /// <summary>
    /// When using a relay, indicates which peer (e.g., Host) should be used to reach this peer.
    /// </summary>
    public PeerId? RelayPeerId { get; private set; }

    public PeerConnection(
        PeerId id,
        DirectMessagePublicKey? identitySigningKey,
        IEnumerable<GrpcEndPoint> grpcEndPoints,
        IReadOnlyList<TlsCertificate> tlsCertificates,
        DateTimeOffset lastSeen,
        PeerId? relayPeerId = null)
    {
        Id = id;
        IdentitySigningKey = identitySigningKey;
        _grpcEndPoints = grpcEndPoints.ToList();
        TlsCertificates = tlsCertificates;
        LastSeen = lastSeen;
        RelayPeerId = relayPeerId;
    }
    
    //todo: add methods to modify the peer connection
    public void UpdateLastSeen(GrpcEndPoint endPoint, DateTimeOffset utcNow)
    {
        _grpcEndPoints.Remove(endPoint);
        _grpcEndPoints.Add(endPoint with { LastSeen = utcNow });
        LastSeen = utcNow;
    }

    public void AddGrpcEndPoint(GrpcEndPoint grpcEndPoint)
    {
        _grpcEndPoints.Add(grpcEndPoint);
    }
    
    public void SetDirectMessagePublicKey(DirectMessagePublicKey directMessagePublicKey)
    {
        IdentitySigningKey = directMessagePublicKey;
    }

    public void SetRelayPeer(PeerId relayPeerId)
    {
        RelayPeerId = relayPeerId;
    }
}

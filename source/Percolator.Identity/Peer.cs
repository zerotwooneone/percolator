namespace Percolator.Identity;

public class Peer
{
    public PeerId Id { get; init; }
    public string IpAddress { get; init; }
    public Endpoint GrpcEndpoint { get; init; }
    public string Thumbprint { get; init; }
    public ConversationId? LastDirectConversationId { get; init; }

    public Peer(PeerId id, string ipAddress, Endpoint grpcEndpoint, string thumbprint, ConversationId? lastDirectConversationId = null)
    {
        Id = id;
        IpAddress = ipAddress;
        GrpcEndpoint = grpcEndpoint;
        Thumbprint = thumbprint;
        LastDirectConversationId = lastDirectConversationId;
    }
}

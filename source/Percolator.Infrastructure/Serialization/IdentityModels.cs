namespace Percolator.Infrastructure.Serialization;

public class PeerModel
{
    public Guid Id { get; set; }
    public string IpAddress { get; set; } = string.Empty;
    public EndpointModel GrpcEndpoint { get; set; } = new();
    public string Thumbprint { get; set; } = string.Empty;
    public Guid? LastDirectConversationId { get; set; }
}

public class EndpointModel
{
    public int Port { get; set; }
}

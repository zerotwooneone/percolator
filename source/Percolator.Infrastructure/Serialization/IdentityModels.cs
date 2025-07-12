namespace Percolator.Infrastructure.Serialization;

public class PeerModel
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public class EndpointModel
{
    public int Port { get; set; }
}

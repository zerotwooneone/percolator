namespace Percolator.Infrastructure.Serialization;

public class PeerModel
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public byte[]? PublicKey { get; set; } = null;
}

public class EndpointModel
{
    public int Port { get; set; }
}

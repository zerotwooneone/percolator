namespace Percolator.Infrastructure.Persistence;

public class TlsCertificateRoutingDbo
{
    public long Id { get; set; }
    public Guid PeerId { get; set; }
    public byte[] RawData { get; set; } = null!;
    public byte[] RawDataHash { get; set; } = null!;
    public DateTimeOffset AddedAtUtc { get; set; }
}

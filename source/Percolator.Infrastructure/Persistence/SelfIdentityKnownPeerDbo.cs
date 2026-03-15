namespace Percolator.Infrastructure.Persistence;

public class SelfIdentityKnownPeerDbo
{
    public int Id { get; set; }
    public int SelfIdentityId { get; set; }
    public Guid PeerId { get; set; }
}

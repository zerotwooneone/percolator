using Percolator.Identity;

namespace Percolator.Infrastructure.Persistence;

public class SelfIdentityKnownPeerDbo
{
    public int Id { get; set; }
    public SelfId SelfIdentityId { get; set; }
    public PeerId PeerId { get; set; }
}

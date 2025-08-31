using System;

namespace Percolator.Infrastructure.Persistence;

public class SelfIdentityDbo
{
    public int Id { get; set; }
    public Guid PeerId { get; set; }
    public string Name { get; set; } = null!;
}

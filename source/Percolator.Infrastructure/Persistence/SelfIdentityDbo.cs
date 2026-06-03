namespace Percolator.Infrastructure.Persistence;

public class SelfIdentityDbo
{
    public int Id { get; set; }
    public Guid PeerId { get; set; }
    public string Name { get; set; } = null!;
    public DateTimeOffset LastUsedUtc { get; set; }
    public int ListeningPort { get; set; }

    // Navigation to the associated keys record (one-to-one)
    public SelfIdentityKeysDbo? Keys { get; set; }
}

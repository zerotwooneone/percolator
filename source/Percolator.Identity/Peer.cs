namespace Percolator.Identity;

public class Peer
{
    public PeerId Id { get; init; }
    public string Name { get; init; }

    public Peer(PeerId id, string name)
    {
        Id = id;
        Name = name;
    }
}

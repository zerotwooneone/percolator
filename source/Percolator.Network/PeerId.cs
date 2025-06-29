using System;

namespace Percolator.Network;

public record PeerId(Guid Value)
{
    public static PeerId NewId() => new(Guid.NewGuid());
}

using System.Collections.Generic;

namespace Percolator.Network.ValueObjects;

public sealed class SelectionTrace
{
    public IReadOnlyList<string> Notes { get; }

    public SelectionTrace(IReadOnlyList<string> notes)
    {
        Notes = notes;
    }
}

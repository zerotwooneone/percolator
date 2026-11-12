using System;
using System.Collections.Generic;

namespace Percolator.Desktop.Common.Services;

/// <summary>Deterministic 5×5 symmetric identicon: 25 occupancy cells + a seed-derived RGB color.</summary>
public sealed record IdenticonModel(IReadOnlyList<bool> Cells, byte Red, byte Green, byte Blue)
{
    public const int GridSize = 5;
    public const int CellCount = GridSize * GridSize;
}

/// <summary>
/// Produces deterministic identicons from an identity seed ([INV-DESKTOP-PRESENTATION-MODELS]).
/// The same seed always produces the same avatar, so no avatar bytes are persisted.
/// </summary>
public interface IIdenticonGenerator
{
    IdenticonModel Generate(Guid seed);
}

using System;
using System.Collections.Generic;
using System.Security.Cryptography;

namespace Percolator.Desktop.Common.Services;

/// <summary>
/// GitHub-style identicon: SHA-256 of the seed fills a mirrored 5×5 grid and selects a
/// saturated foreground color. Deterministic — the persona avatar can always be regenerated
/// from the identity id.
/// </summary>
public sealed class IdenticonGenerator : IIdenticonGenerator
{
    public IdenticonModel Generate(Guid seed)
    {
        var hash = SHA256.HashData(seed.ToByteArray());

        // 3 bytes for the color, then 15 bits for the left-half occupancy (5 rows × 3 cols).
        var red = Saturate(hash[0]);
        var green = Saturate(hash[1]);
        var blue = Saturate(hash[2]);

        var cells = new bool[IdenticonModel.CellCount];
        var bitIndex = 3;
        for (var row = 0; row < IdenticonModel.GridSize; row++)
        {
            for (var col = 0; col < 3; col++)
            {
                var filled = ((hash[bitIndex / 8] >> (bitIndex % 8)) & 1) == 1;
                cells[row * IdenticonModel.GridSize + col] = filled;
                cells[row * IdenticonModel.GridSize + (IdenticonModel.GridSize - 1 - col)] = filled;
                bitIndex++;
            }
        }

        return new IdenticonModel(cells, red, green, blue);
    }

    // Keep colors readable on both themes: pull the channel into the mid-high range.
    private static byte Saturate(byte channel) => (byte)(96 + (channel % 128));
}

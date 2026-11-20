using System;
using System.IO;
using SkiaSharp;

namespace Percolator.Simulator.Features.Topology;

internal static class IdenticonGenerator
{
    private const int GridSize = 5;
    private const int ImageSize = 64;

    public static byte[] CreatePng(uint seed)
    {
        // Deterministic pseudo-randomness from seed
        var hash = BitConverter.GetBytes(seed);
        var r = (byte)(hash[0] % 180 + 40);
        var g = (byte)(hash[1] % 180 + 40);
        var b = (byte)(hash[2] % 180 + 40);
        var fgColor = new SKColor(r, g, b);
        var bgColor = new SKColor(30, 30, 30);

        using var bitmap = new SKBitmap(ImageSize, ImageSize);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(bgColor);

        using var paint = new SKPaint
        {
            Color = fgColor,
            IsAntialias = false,
            Style = SKPaintStyle.Fill
        };

        var cellSize = ImageSize / GridSize;
        var padding = (ImageSize - cellSize * GridSize) / 2;

        // Generate 5x5 symmetric grid (3 unique columns mirrored)
        var bitIndex = 0;
        for (var col = 0; col < 3; col++)
        {
            for (var row = 0; row < GridSize; row++)
            {
                var isFilled = ((seed >> (bitIndex % 32)) & 1) == 1;
                bitIndex++;

                if (isFilled)
                {
                    // Left column
                    canvas.DrawRect(padding + col * cellSize, padding + row * cellSize, cellSize, cellSize, paint);
                    // Mirror to right column
                    var mirroredCol = GridSize - 1 - col;
                    if (mirroredCol != col)
                    {
                        canvas.DrawRect(padding + mirroredCol * cellSize, padding + row * cellSize, cellSize, cellSize, paint);
                    }
                }
            }
        }

        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }
}

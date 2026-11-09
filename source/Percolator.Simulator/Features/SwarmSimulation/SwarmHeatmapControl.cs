using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Immutable;

namespace Percolator.Simulator.Features.SwarmSimulation;

internal sealed class SwarmHeatmapControl : Control
{
    private static readonly IBrush MissingBrush = new ImmutableSolidColorBrush(Color.FromRgb(45, 45, 48));
    private static readonly IBrush InFlightBrush = new ImmutableSolidColorBrush(Color.FromRgb(33, 150, 243));
    private static readonly IBrush VerifiedBrush = new ImmutableSolidColorBrush(Color.FromRgb(76, 175, 80));
    private static readonly IBrush CorruptedBrush = new ImmutableSolidColorBrush(Color.FromRgb(244, 67, 54));
    private static readonly IPen GridPen = new ImmutablePen(new ImmutableSolidColorBrush(Color.FromRgb(25, 25, 28)), 1);

    public static readonly StyledProperty<IReadOnlyList<PieceState>?> PiecesProperty =
        AvaloniaProperty.Register<SwarmHeatmapControl, IReadOnlyList<PieceState>?>(nameof(Pieces));

    public static readonly StyledProperty<int> TotalPiecesProperty =
        AvaloniaProperty.Register<SwarmHeatmapControl, int>(nameof(TotalPieces), 0);

    public static readonly StyledProperty<int> VerifiedPiecesProperty =
        AvaloniaProperty.Register<SwarmHeatmapControl, int>(nameof(VerifiedPieces), 0);

    static SwarmHeatmapControl()
    {
        AffectsRender<SwarmHeatmapControl>(PiecesProperty, TotalPiecesProperty, VerifiedPiecesProperty);
    }

    public IReadOnlyList<PieceState>? Pieces
    {
        get => GetValue(PiecesProperty);
        set => SetValue(PiecesProperty, value);
    }

    public int TotalPieces
    {
        get => GetValue(TotalPiecesProperty);
        set => SetValue(TotalPiecesProperty, value);
    }

    public int VerifiedPieces
    {
        get => GetValue(VerifiedPiecesProperty);
        set => SetValue(VerifiedPiecesProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        var bounds = Bounds;
        if (bounds.Width <= 0 || bounds.Height <= 0) return;

        var pieces = Pieces;
        int count = pieces?.Count ?? TotalPieces;
        if (count <= 0)
        {
            // Draw dark background placeholder
            context.FillRectangle(MissingBrush, bounds);
            return;
        }

        // Compute optimal grid dimensions
        double width = bounds.Width;
        double height = bounds.Height;
        double aspectRatio = width / Math.Max(1.0, height);

        int cols = Math.Max(1, (int)Math.Ceiling(Math.Sqrt(count * aspectRatio)));
        int rows = Math.Max(1, (count + cols - 1) / cols);

        double cellWidth = width / cols;
        double cellHeight = height / rows;
        bool drawBorders = cellWidth >= 4 && cellHeight >= 4;

        for (int i = 0; i < count; i++)
        {
            int col = i % cols;
            int row = i / cols;

            PieceState state = pieces != null && i < pieces.Count ? pieces[i] : PieceState.Missing;
            IBrush brush = state switch
            {
                PieceState.Verified => VerifiedBrush,
                PieceState.InFlight => InFlightBrush,
                PieceState.Corrupted => CorruptedBrush,
                _ => MissingBrush
            };

            Rect cellRect = new(col * cellWidth, row * cellHeight, cellWidth, cellHeight);
            context.FillRectangle(brush, cellRect);
            if (drawBorders)
            {
                context.DrawRectangle(null, GridPen, cellRect);
            }
        }
    }
}

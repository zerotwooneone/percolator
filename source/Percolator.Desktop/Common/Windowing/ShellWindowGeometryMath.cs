// SPECIFICATION GOVERNANCE: Read Percolator.Desktop/spec/CodeBehind.spec.md before modifying this file.

using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;

namespace Percolator.Desktop.Common.Windowing;

/// <summary>
/// Pure geometry rules for WIN-02 — unit-testable without an Avalonia window. All rects are
/// in DIP space; callers convert <c>PixelRect</c> working areas with the screen's
/// <c>Scaling</c> before calling.
/// </summary>
internal static class ShellWindowGeometryMath
{
    /// <summary>
    /// Clamps <paramref name="candidate"/> onto the visible working areas:
    /// <list type="number">
    /// <item>If it intersects any working area by ≥ 50% of its area — keep it.</item>
    /// <item>Else if it partially intersects — shift onto the best-overlapping screen
    /// (size clamped to that working area when it overflows).</item>
    /// <item>Else — the primary working area's centered rect at the saved size, clamped
    /// to the working area when the saved size exceeds it.</item>
    /// </list>
    /// </summary>
    public static Rect ClampToWorkingArea(Rect candidate, IReadOnlyList<Rect> workingAreas)
    {
        if (workingAreas.Count == 0 || candidate.Width <= 0 || candidate.Height <= 0)
        {
            return candidate;
        }

        var candidateArea = candidate.Width * candidate.Height;
        var bestArea = default(Rect);
        var bestOverlap = 0.0;

        foreach (var area in workingAreas)
        {
            var overlap = Intersection(candidate, area);
            var overlapArea = overlap.Width * overlap.Height;
            if (overlapArea > bestOverlap)
            {
                bestOverlap = overlapArea;
                bestArea = area;
            }
        }

        // Rule 1 — already mostly on-screen; respect the user's placement exactly.
        if (bestOverlap >= candidateArea * 0.5)
        {
            return candidate;
        }

        // Rule 2 — partial overlap: pull fully onto the screen it touches most.
        if (bestOverlap > 0)
        {
            var w = Math.Min(candidate.Width, bestArea.Width);
            var h = Math.Min(candidate.Height, bestArea.Height);
            var x = Math.Clamp(candidate.X, bestArea.X, bestArea.Right - w);
            var y = Math.Clamp(candidate.Y, bestArea.Y, bestArea.Bottom - h);
            return new Rect(x, y, w, h);
        }

        // Rule 3 — detached monitor: recenter on the primary working area at saved size.
        var primary = workingAreas[0];
        var pw = Math.Min(candidate.Width, primary.Width);
        var ph = Math.Min(candidate.Height, primary.Height);
        return new Rect(
            primary.X + (primary.Width - pw) / 2,
            primary.Y + (primary.Height - ph) / 2,
            pw,
            ph);
    }

    /// <summary>
    /// Decides whether a live geometry event should update the persisted record.
    /// <c>Minimized</c> is skipped (position is stale); <c>Maximized</c>/<c>FullScreen</c>
    /// update only the state, keeping the last Normal restore rect so the shell restores
    /// usable bounds next launch.
    /// </summary>
    /// <param name="restoreGeometry">The last captured record (may be <c>null</c> on first capture).</param>
    /// <param name="state">Current <see cref="WindowState"/>.</param>
    /// <param name="boundsDip">Current window rect in DIPs (meaningful only when Normal).</param>
    /// <param name="result">The record to persist, when the call returns <c>true</c>.</param>
    public static bool TryCapture(
        ShellWindowGeometry? restoreGeometry,
        WindowState state,
        Rect boundsDip,
        out ShellWindowGeometry? result)
    {
        result = null;

        if (state == WindowState.Minimized)
        {
            return false;
        }

        if (state == WindowState.Normal)
        {
            result = new ShellWindowGeometry(boundsDip.X, boundsDip.Y, boundsDip.Width, boundsDip.Height, WindowState.Normal);
            return true;
        }

        // Maximized/FullScreen — retain the restore rect; record the state alone.
        result = restoreGeometry is { } restore
            ? restore with { State = state }
            : new ShellWindowGeometry(boundsDip.X, boundsDip.Y, boundsDip.Width, boundsDip.Height, state);
        return true;
    }

    private static Rect Intersection(Rect a, Rect b)
    {
        var x1 = Math.Max(a.X, b.X);
        var y1 = Math.Max(a.Y, b.Y);
        var x2 = Math.Min(a.Right, b.Right);
        var y2 = Math.Min(a.Bottom, b.Bottom);

        return x2 <= x1 || y2 <= y1
            ? new Rect(0, 0, 0, 0)
            : new Rect(x1, y1, x2 - x1, y2 - y1);
    }
}

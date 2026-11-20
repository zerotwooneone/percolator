// SPECIFICATION GOVERNANCE: Read Percolator.Desktop/spec/ViewModel.spec.md before modifying this file.

using System.Collections.Generic;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Percolator.Desktop.Common.Services;

namespace Percolator.Desktop.Features.Startup.Models;

/// <summary>
/// Bindable avatar projection for a generated identicon: the 25-cell occupancy grid plus
/// the seed-derived foreground brush. The only place an Avalonia brush is built from a
/// pure <see cref="IdenticonModel"/>, keeping VMs/models otherwise UI-agnostic.
/// </summary>
public sealed record AvatarPresentation(IReadOnlyList<bool> Cells, IBrush Color)
{
    public static AvatarPresentation FromIdenticon(IdenticonModel identicon) =>
        new(identicon.Cells,
            new ImmutableSolidColorBrush(Avalonia.Media.Color.FromRgb(identicon.Red, identicon.Green, identicon.Blue)));
}

using Avalonia;
using Avalonia.Media;
using FluentAvalonia.UI.Controls;

namespace Percolator.Desktop.Common.Controls;

/// <summary>
/// A specialized FontIcon configured to render Nerd Font icons using strongly typed NerdGlyph values.
/// </summary>
public class NerdIcon : FontIcon
{
    public static readonly StyledProperty<NerdGlyph> GlyphTypeProperty =
        AvaloniaProperty.Register<NerdIcon, NerdGlyph>(nameof(GlyphType), NerdGlyph.None);

    public static readonly FontFamily DefaultNerdFont = new("avares://Percolator.Desktop/Assets/Fonts#FiraCode Nerd Font Mono, FiraCode Nerd Font, Consolas, monospace");

    static NerdIcon()
    {
        FontFamilyProperty.OverrideDefaultValue<NerdIcon>(DefaultNerdFont);
    }

    public NerdIcon()
    {
        FontFamily = DefaultNerdFont;
    }

    public NerdGlyph GlyphType
    {
        get => GetValue(GlyphTypeProperty);
        set => SetValue(GlyphTypeProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == GlyphTypeProperty)
        {
            Glyph = change.GetNewValue<NerdGlyph>().ToGlyphString();
        }
    }
}

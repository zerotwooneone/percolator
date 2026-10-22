using Avalonia;
using Avalonia.Media;
using FluentAvalonia.UI.Controls;

namespace Percolator.Desktop.Common.Controls;

/// <summary>
/// A specialized FontIconSource for use in NavigationViewItem, CommandBarButton, etc.,
/// rendering Nerd Font glyphs with strongly typed NerdGlyph.
/// </summary>
public class NerdIconSource : FontIconSource
{
    public static readonly StyledProperty<NerdGlyph> GlyphTypeProperty =
        AvaloniaProperty.Register<NerdIconSource, NerdGlyph>(nameof(GlyphType), NerdGlyph.None);

    public static readonly FontFamily DefaultNerdFont = new("avares://Percolator.Desktop/Assets/Fonts#FiraCode Nerd Font Mono, FiraCode Nerd Font, Consolas, monospace");

    static NerdIconSource()
    {
        FontFamilyProperty.OverrideDefaultValue<NerdIconSource>(DefaultNerdFont);
    }

    public NerdIconSource()
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

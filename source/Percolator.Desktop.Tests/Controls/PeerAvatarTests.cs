using System.Globalization;
using Avalonia.Media;
using FluentAssertions;
using NUnit.Framework;
using Percolator.Desktop.Common.Controls;
using Percolator.Desktop.Common.Converters;

namespace Percolator.Desktop.Tests.Controls;

[TestFixture]
public sealed class PeerAvatarTests
{
    [Test]
    public void IdentityHashToBrushConverter_ReturnsConsistentBrush_ForSameKey()
    {
        var converter = IdentityHashToBrushConverter.Instance;

        var brush1 = converter.Convert("0123456789ABCDEF", typeof(IBrush), null, CultureInfo.InvariantCulture);
        var brush2 = converter.Convert("0123456789ABCDEF", typeof(IBrush), null, CultureInfo.InvariantCulture);

        brush1.Should().NotBeNull();
        brush1.Should().BeSameAs(brush2, "same key must deterministically yield the exact same color palette brush");
    }

    [Test]
    public void IdentityHashToBrushConverter_HandlesNullGracefully()
    {
        var converter = IdentityHashToBrushConverter.Instance;
        var brush = converter.Convert(null, typeof(IBrush), null, CultureInfo.InvariantCulture);
        brush.Should().NotBeNull();
    }

    [Test]
    public void NerdGlyphExtensions_ToGlyphString_ConvertsUtf32Correctly()
    {
        var shield = NerdGlyph.Shield.ToGlyphString();
        shield.Should().NotBeNullOrEmpty();

        var none = NerdGlyph.None.ToGlyphString();
        none.Should().BeEmpty();
    }
}

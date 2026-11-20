namespace Percolator.Desktop.Common.Controls;

/// <summary>
/// Strongly-typed Nerd Font glyph definitions for P2P mesh network, routing, security, and telemetry iconography.
/// </summary>
public enum NerdGlyph
{
    None = 0,
    DirectRoute = 0xF0B45,
    RelayedRoute = 0xF50F,
    MeshNetwork = 0xEF09,
    Latency = 0xF0128,
    TreeRouting = 0xF104A,
    CloudLock = 0xF11F1,
    Swarm = 0xF00D0,
    Key = 0xF0306,
    Shield = 0xF04C5,
    Lock = 0xF033E,
    Unlock = 0xF033F,
    Check = 0xF012C,
    Circle = 0xF012F,
    Server = 0xF0493,
    Database = 0xF01BC,
    Terminal = 0xF04F8,
    RadioTower = 0xF000F,
    Wifi = 0xF05A9,
    WifiOff = 0xF05AA
}

public static class NerdGlyphExtensions
{
    public static string ToGlyphString(this NerdGlyph glyph)
    {
        return glyph == NerdGlyph.None ? string.Empty : char.ConvertFromUtf32((int)glyph);
    }
}

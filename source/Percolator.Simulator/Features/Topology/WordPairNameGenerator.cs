using System;

namespace Percolator.Simulator.Features.Topology;

internal static class WordPairNameGenerator
{
    private static readonly string[] Adjectives =
    {
        "Amber", "Azure", "Bold", "Brave", "Bright", "Calm", "Crimson", "Crystal",
        "Dawn", "Deft", "Eager", "Emerald", "Fierce", "Fleet", "Frost", "Gentle",
        "Golden", "Grand", "Hardy", "Ivory", "Jade", "Keen", "Lively", "Lunar",
        "Mighty", "Noble", "Onyx", "Proud", "Quick", "Quiet", "Radiant", "Rapid",
        "Ruby", "Rustic", "Sage", "Shadow", "Silent", "Silver", "Solar", "Stalwart",
        "Steady", "Storm", "Subtle", "Swift", "Tidal", "Topaz", "Valiant", "Vivid",
        "Wild", "Wise", "Zenith", "Zephyr"
    };

    private static readonly string[] Nouns =
    {
        "Badger", "Bear", "Beaver", "Bison", "Cat", "Cobra", "Condor", "Cougar",
        "Crane", "Deer", "Dolphin", "Eagle", "Falcon", "Ferret", "Finch", "Fox",
        "Hawk", "Heron", "Jaguar", "Leopard", "Lion", "Lynx", "Marten", "Otter",
        "Owl", "Panther", "Pelican", "Raven", "Robin", "Seal", "Shark", "Sparrow",
        "Tiger", "Viper", "Walrus", "Whale", "Wolf", "Wolverine", "Wren"
    };

    private static readonly Random Rng = new();

    public static string Generate()
    {
        var adj = Adjectives[Rng.Next(Adjectives.Length)];
        var noun = Nouns[Rng.Next(Nouns.Length)];
        return $"{adj} {noun}";
    }
}
